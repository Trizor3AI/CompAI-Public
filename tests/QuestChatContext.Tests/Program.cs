using System.Collections.Immutable;
using System.Text.Json;
using CompAI;

var normal = Snapshot(Enumerable.Range(1, 5532).Select(i => Quest(i,
    i == 100 ? "Phantoms to Fillet" : "Synthetic Quest " + i, i <= 3)));
var selected = QuestChatContext.Create(normal, false, "Have I completed Phantoms to Fillet?");
Check(selected.rows.Length == 4 && selected.rows.Any(q => q.native_id == 100), "Select active and relevant names only");
Check(selected.catalog_rows == 5532 && selected.observed_completed_count == 2766, "Keep bounded catalog summary");
Check(selected.rows.Single(q => q.native_id == 100).completed == true, "Preserve observed completion");
Check(selected.rows.Single(q => q.native_id == 1).completed == false, "Preserve negative observation, not null");
var byId = QuestChatContext.Create(normal, true, "What about quest 101 or 65638?");
Check(byId.rows.Any(q => q.native_id == 101) && byId.rows.Any(q => q.native_id == 102), "Exact native and row ID lookup");
Check(!byId.rows.Any(q => q.native_id == 10), "IDs must not match substrings");
Check(byId.stale, "Preserve stale label");
var unrelated = QuestChatContext.Create(normal, false, "What quests have I done?");
Check(unrelated.rows.Length == 3 && unrelated.matched_count == 0, "Generic words must not dump completed catalog");
var wide = Snapshot(Enumerable.Range(1, 80).Select(i => Quest(i,
    "Bounded " + new string('\u754c', 300) + i, i <= 30) with { JournalText = new string('\u754c', 500) }));
var bounded = QuestChatContext.Create(wide, true, "Bounded");
Check(JsonSerializer.SerializeToUtf8Bytes(bounded).Length <= QuestChatContext.MaxBytes, "Escaped UTF-8 budget");
Check(bounded.active_count == 30 && bounded.matched_count == 50, "Counts include omitted rows");
Check(bounded.rows.Count(q => q.accepted) + bounded.active_omitted == 30 &&
      bounded.rows.Count(q => !q.accepted) + bounded.matched_omitted == 50, "Omission accounting");
Check(bounded.rows.All(q => q.name_truncated && q.name!.Length <= 200), "Bound huge names explicitly");
var unknown = QuestChatContext.Create(Snapshot(new[] {
    Quest(6009, "Unmapped", true) with { RowId = null, Completed = null, JournalText = null },
}), true, "Unmapped");
Check(unknown.rows.Single().completed is null && unknown.rows.Single().journal_status == "unavailable", "Unknown remains unknown");
var journal = QuestChatContext.Create(Snapshot(new[] {
    Quest(1, "Plain Journal", true) with { JournalText = "An exact sequence paragraph." },
    Quest(2, "Long Journal", true) with { JournalText = new string('x', 513) },
}), false, "Journal");
Check(journal.rows[0].journal_status == "included" && journal.rows[0].journal_text != null &&
      journal.rows[1].journal_status == "omitted" && journal.rows[1].journal_text == null, "Journal bound and labels");
var emptyText = QuestChatContext.Create(Snapshot(new[] {
    Quest(1, "\0 \0", true) with { JournalText = "\0 \0" },
}), false, "Progress");
Check(emptyText.rows[0].name is null && emptyText.rows[0].journal_text is null &&
      emptyText.rows[0].journal_status == "unavailable", "NUL-only strings become absent, never invalid empty text");
if (args.Length == 2 && args[0] == "--fixture")
    File.WriteAllText(args[1], JsonSerializer.Serialize(new[] { selected, byId, unrelated, bounded, unknown, journal, emptyText }));
Console.WriteLine("7 quest chat selection/budget groups passed (synthetic only).");

static QuestObservation Quest(int id, string name, bool active) => new(65536u + (uint)id, (ushort)id, name,
    false, active, id % 2 == 0, active ? (byte)3 : null, active ? (byte)0 : null,
    active ? (byte)1 : null, ImmutableArray<byte>.Empty, null);
static QuestSnapshot Snapshot(IEnumerable<QuestObservation> rows)
{
    var quests = rows.ToImmutableArray();
    return new(DateTimeOffset.UtcNow.AddMinutes(-5), 0, "synthetic", "limited", "limited", null,
        quests.Length, quests.Length, 30, quests);
}
static void Check(bool ok, string note) { if (!ok) throw new Exception(note); }
