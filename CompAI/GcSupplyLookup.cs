using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace CompAI;

internal sealed record GcSupplyLookupSummary(string scope);
internal sealed record GcSupplyLookupRow(int position, uint class_job_id, string class_job_abbreviation,
    string category, uint item_id, string name, int quantity_requested, int experience_reward,
    int seal_reward, bool? turn_in_available, bool? bonus_reward);
internal sealed record GcSupplyLookupResult(string scope, string captured_at_utc, bool available,
    string? unavailable_reason, string coverage, GcSupplyLookupRow[] entries, int matched_count, bool truncated, string readiness);

internal sealed class GcSupplyLookup(GcSupplySnapshot snapshot)
{
    internal const string Scope = "observed_gc_daily_requests";
    internal const int MaxBytes = 20000;
    internal GcSupplyLookupSummary Summary => new(Scope);
    internal static void ValidateArguments(JsonElement arguments) => _ = Parse(arguments);
    internal GcSupplyLookupResult Query(JsonElement arguments)
    {
        var category = Parse(arguments);
        var available = snapshot.Available && snapshot.IsFresh(DateTimeOffset.UtcNow);
        var rows = available ? snapshot.Entries.Where(entry => category == "all" || entry.Category == category)
            .Select(entry => new GcSupplyLookupRow(entry.Position, entry.ClassJobId, entry.ClassJobAbbreviation,
                entry.Category, entry.ItemId, entry.Name, entry.QuantityRequested, entry.ExperienceReward,
                entry.SealReward, entry.TurnInAvailable, entry.BonusReward)).ToArray() : [];
        return new(Scope, snapshot.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture), available,
            available ? null : snapshot.UnavailableReason ?? "The observation expired; read the visible Grand Company window again.",
            GcSupplySnapshot.Coverage + (snapshot.LastObserved
                ? " Last observed during this login; may be stale, expires after 15 minutes. Eligibility is unknown; reopen the delivery menu to refresh." : ""),
            rows, rows.Length, false, !available ? "unavailable" : snapshot.LastObserved ? "last_observed_menu" : "visible_menu_observation");
    }

    private static string Parse(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid Grand Company lookup arguments.");
        var category = "all";
        var seen = false;
        foreach (var field in arguments.EnumerateObject())
        {
            if (field.Name != "category" || seen || field.Value.ValueKind != JsonValueKind.String ||
                field.Value.GetString() is not ("all" or "supply" or "provisioning"))
                throw new ArgumentException("Invalid Grand Company lookup arguments.");
            category = field.Value.GetString()!;
            seen = true;
        }
        return category;
    }
}
