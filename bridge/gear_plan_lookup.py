"""Bounded normalization of public xivgear shares. No HTTP or private gear uploads.

Shape reference: xiv-gear-planner/gear-planner API_DOC.md and geartypes.ts.
Descriptions, simulation settings/results and arbitrary nested fields are omitted.
"""
import math
import re
from urllib.parse import parse_qsl, unquote, urlsplit

UUID = r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
SLOTS = ("Weapon", "OffHand", "Head", "Body", "Hand", "Legs", "Feet", "Ears", "Neck", "Wrist", "RingLeft", "RingRight")
STATS = ("strength", "dexterity", "intelligence", "mind", "vitality", "crit", "determination", "dhit",
         "skillspeed", "spellspeed", "piety", "tenacity", "wdPhys", "wdMag", "weaponDelay", "hp",
         "craftsmanship", "control", "cp", "gathering", "perception", "gp")
SUBSTATS = ("crit", "determination", "dhit", "skillspeed", "spellspeed", "piety", "tenacity")
JOBS = frozenset("PLD WAR DRK GNB WHM SCH AST SGE MNK DRG NIN SAM RPR VPR BST BRD MCH DNC BLM SMN RDM PCT BLU CRP BSM ARM GSM LTW WVR ALC CUL MIN BTN FSH".split())
MAX_SETS = 1000
SUMMARY_LIMIT = 24
COVERAGE = ("Reference plan, not current equipment or ownership. Computed stats, when requested, are xivgear's "
            "calculation using the plan's level, food, medicine, party bonus, race and sync; null settings are unspecified. "
            "Food/medicine use xivgear's HQ bonuses, not observed consumption. "
            "force_nq=false uses planner-default item quality, not observed player HQ. "
            "No DPS simulation, optimal-meld proof, or adjusted job GCD is returned. Names are untrusted data.")


def require(condition):
    if not condition:
        raise ValueError("Unsupported public gear plan")


def number(value, maximum=1000000):
    require(type(value) in (int, float) and math.isfinite(value) and 0 <= value <= maximum)
    return value


def integer(value, low=0, high=999999):
    require(type(value) is int and low <= value <= high)
    return value


def text(value, maximum=128):
    require(isinstance(value, str) and len(value) <= maximum and not any(ord(c) < 32 or ord(c) == 127 for c in value))
    return value


def parse_share_url(value):
    text(value, 512)
    require(not any(c.isspace() for c in value) and "\\" not in value)
    parts = urlsplit(value)
    require(parts.scheme == "https" and parts.hostname in ("xivgear.app", "share.xivgear.app") and
            parts.port is None and parts.username is None and parts.password is None)
    pairs = parse_qsl(parts.query, strict_parsing=True)
    query = dict(pairs)
    require(len(query) == len(pairs) and set(query) <= {"page", "onlySetIndex", "selectedIndex"})
    require(not ("onlySetIndex" in query and "selectedIndex" in query))
    index_text = query.get("onlySetIndex", query.get("selectedIndex"))
    index = None
    if index_text is not None:
        require(re.fullmatch(r"[0-9]{1,3}", index_text) is not None)
        index = int(index_text)
    candidate = None
    if parts.hostname == "share.xivgear.app":
        require(not parts.fragment and "page" not in query)
        match = re.fullmatch(r"/share/(" + UUID + r")/?", parts.path)
        if match: candidate = match[1]
    elif parts.path in ("", "/"):
        require(not (parts.fragment and "page" in query))
        route = unquote(parts.fragment) if parts.fragment else query.get("page", "")
        match = re.fullmatch(r"(?:/?sl/|sl\|)(" + UUID + r")", route)
        if match: candidate = match[1]
    else:
        require(not parts.fragment and "page" not in query)
        match = re.fullmatch(r"/sl/(" + UUID + r")/?", parts.path)
        if match: candidate = match[1]
    require(candidate is not None)
    return candidate.lower(), index


def share_ids_in_texts(texts):
    """Only literal approved user-message links authorize this question's public shares."""
    result = set()
    for value in texts:
        if not isinstance(value, str): continue
        for match in re.finditer(r"https://[^\s<>\"']+", value):
            try:
                share_id, _ = parse_share_url(match[0].rstrip(".,;:!?)]}"))
                result.add(share_id)
            except ValueError:
                continue
    return frozenset(result)


def _job(value):
    if value is None: return None
    require(value in JOBS)
    return value


def _optional_integer(value, low, high):
    return None if value is None else integer(value, low, high)


def _row_job(row, sheet):
    override = row.get("jobOverride")
    return _job(override if override is not None else row.get("job") or sheet.get("job"))


def _stats(value):
    require(isinstance(value, dict))
    return {key: number(value[key]) for key in STATS if key in value}


def _items(value):
    require(isinstance(value, dict) and set(value) <= set(SLOTS))
    result = []
    for slot in SLOTS:
        row = value.get(slot)
        if row is None: continue
        require(isinstance(row, dict))
        require(type(row.get("forceNq", False)) is bool)
        materia = row.get("materia", [])
        require(isinstance(materia, list) and len(materia) <= 5)
        ids = []
        for meld in materia:
            require(isinstance(meld, dict))
            meld_id = integer(meld.get("id"), -1)
            ids.append(meld_id if meld_id > 0 else None)
        relic = row.get("relicStats")
        require(relic is None or isinstance(relic, dict) and set(relic) <= set(SUBSTATS))
        result.append({"slot": slot, "item_id": integer(row.get("id"), 1), "force_nq": row.get("forceNq", False), "materia_item_ids": ids,
                       "relic_stats": None if relic is None else {k: number(v, 9999) for k, v in relic.items()}})
    return result


def normalize_base(raw, share_id, requested_index=None):
    require(isinstance(raw, dict))
    # Custom items/foods can use arbitrary local IDs; don't mistake them for installed Item rows.
    require(not raw.get("customItems") and not raw.get("customFoods") and not raw.get("specialStats"))
    rows = raw.get("sets", [raw])
    require(isinstance(rows, list) and 1 <= len(rows) <= MAX_SETS)
    metadata = []
    for index, row in enumerate(rows):
        require(isinstance(row, dict) and type(row.get("isSeparator", False)) is bool)
        if len(metadata) < SUMMARY_LIMIT:
            metadata.append({"index": index, "name": text(row.get("name", "")),
                             "job": _row_job(row, raw),
                             "separator": row.get("isSeparator", False)})
    selected_index = requested_index if requested_index is not None else (0 if len(rows) == 1 else None)
    selected = None
    if selected_index is not None:
        integer(selected_index, 0, len(rows) - 1)
        row = rows[selected_index]
        require(not row.get("isSeparator", False) and not row.get("customItems") and
                not row.get("customFoods") and not row.get("specialStats"))
        selected = {"index": selected_index, "name": text(row.get("name", "")),
                    "job": _row_job(row, raw),
                    "level": _optional_integer(row.get("level", raw.get("level")), 1, 100),
                    "party_bonus_percent": _optional_integer(row.get("partyBonus", raw.get("partyBonus")), 0, 5),
                    "race": None if row.get("race", raw.get("race")) is None else text(row.get("race", raw.get("race")), 32),
                    "item_level_sync": _optional_integer(row.get("ilvlSync", raw.get("ilvlSync")), 1, 9999),
                    "food_item_id": _optional_integer(row.get("food"), 1, 999999),
                    "medicine_item_id": _optional_integer(row.get("medicine"), 1, 999999),
                    "items": _items(row.get("items")), "computed_stats": None, "food_stat_bonuses": None,
                    "medicine_stat_bonuses": None}
    return {"share_id": share_id, "total_sets": len(rows), "sets": metadata,
            "more_sets": len(rows) > len(metadata), "selected": selected, "coverage": COVERAGE}


def apply_computed(data, raw):
    require(isinstance(raw, dict) and isinstance(raw.get("sets"), list) and len(raw["sets"]) == 1)
    selected = data["selected"]
    row = raw["sets"][0]
    require(selected is not None and isinstance(row, dict) and _items(row.get("items")) == selected["items"])
    require(row.get("food") == selected["food_item_id"] and row.get("medicine") == selected["medicine_item_id"])
    require(not raw.get("customItems") and not raw.get("customFoods") and not raw.get("specialStats"))
    computed = row.get("computedStats")
    require(isinstance(computed, dict) and _job(computed.get("job")) == selected["job"])
    require(computed.get("level") == selected["level"])
    selected["computed_stats"] = _stats(computed)
    food = computed.get("effectiveFoodBonuses")
    selected["food_stat_bonuses"] = None if food is None else _stats(food)
    medicine = computed.get("effectiveMedicineBonuses")
    selected["medicine_stat_bonuses"] = None if medicine is None else _stats(medicine)
    # Never attach computed totals to different assumptions. Fill effective defaults
    # only when the original export left the corresponding setting unspecified.
    for source, target in (("partyBonus", "party_bonus_percent"), ("ilvlSync", "item_level_sync"), ("race", "race")):
        value = raw.get(source)
        if value is not None:
            value = text(value, 32) if source == "race" else integer(value, 0 if source == "partyBonus" else 1, 5 if source == "partyBonus" else 9999)
            require(selected[target] is None or selected[target] == value)
            selected[target] = value
        else:
            require(selected[target] is None)
    return data


def validate_data(data):
    require(isinstance(data, dict) and set(data) == {"share_id", "total_sets", "sets", "more_sets", "selected", "coverage"})
    require(isinstance(data["share_id"], str) and re.fullmatch(UUID.lower(), data["share_id"]) is not None)
    total = integer(data["total_sets"], 1, MAX_SETS)
    require(type(data["more_sets"]) is bool and data["more_sets"] == (total > SUMMARY_LIMIT))
    require(data["coverage"] == COVERAGE and isinstance(data["sets"], list) and len(data["sets"]) == min(total, SUMMARY_LIMIT))
    for i, row in enumerate(data["sets"]):
        require(isinstance(row, dict) and set(row) == {"index", "name", "job", "separator"})
        require(type(row["index"]) is int and row["index"] == i and type(row["separator"]) is bool)
        text(row["name"]); _job(row["job"])
    row = data["selected"]
    if row is None: return data
    require(isinstance(row, dict) and set(row) == {"index", "name", "job", "level", "party_bonus_percent", "race", "item_level_sync", "food_item_id", "medicine_item_id", "items", "computed_stats", "food_stat_bonuses", "medicine_stat_bonuses"})
    integer(row["index"], 0, total - 1); text(row["name"]); _job(row["job"])
    _optional_integer(row["level"], 1, 100); _optional_integer(row["party_bonus_percent"], 0, 5)
    if row["race"] is not None: text(row["race"], 32)
    _optional_integer(row["item_level_sync"], 1, 9999); _optional_integer(row["food_item_id"], 1, 999999)
    _optional_integer(row["medicine_item_id"], 1, 999999)
    items = row["items"]
    require(isinstance(items, list) and len(items) <= len(SLOTS))
    seen = set()
    for item in items:
        require(isinstance(item, dict) and set(item) == {"slot", "item_id", "force_nq", "materia_item_ids", "relic_stats"})
        require(item["slot"] in SLOTS and item["slot"] not in seen); seen.add(item["slot"])
        integer(item["item_id"], 1)
        require(type(item["force_nq"]) is bool)
        require(isinstance(item["materia_item_ids"], list) and len(item["materia_item_ids"]) <= 5)
        for meld in item["materia_item_ids"]: _optional_integer(meld, 1, 999999)
        relic = item["relic_stats"]
        require(relic is None or isinstance(relic, dict) and set(relic) <= set(SUBSTATS))
        if relic is not None:
            for value in relic.values(): number(value, 9999)
    for field in ("computed_stats", "food_stat_bonuses", "medicine_stat_bonuses"):
        value = row[field]
        if value is not None:
            require(isinstance(value, dict) and set(value) <= set(STATS)); _stats(value)
    return data
