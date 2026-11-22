using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace CompAI;

internal sealed record NpcTargetLookupSummary(string scope, bool allow_npcs, bool allow_players_and_objects);
internal sealed record NpcTargetLookupPlayer(uint? home_world_id, string? home_world_name, uint? class_job_id,
    string? class_job_name, int? level, string? lodestone_search_url);
internal sealed record NpcTargetLookupRow(string name, uint? data_id, string kind, NpcTargetLookupPlayer? player);
internal sealed record NpcTargetLookupResult(string scope, string captured_at_utc, bool available,
    string? unavailable_reason, string coverage, NpcTargetLookupRow? target);

internal sealed class NpcTargetLookup(NpcTargetSnapshot snapshot)
{
    internal const string Scope = "observed_selected_target";
    internal const int MaxBytes = 6000;
    internal NpcTargetLookupSummary Summary => new(Scope, snapshot.AllowNpcs, snapshot.AllowPlayersAndObjects);
    internal static void ValidateArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any())
            throw new ArgumentException("Selected-target lookup takes no arguments.", nameof(arguments));
    }
    internal NpcTargetLookupResult Query(JsonElement arguments)
    {
        ValidateArguments(arguments);
        var available = snapshot.Available && snapshot.IsFresh(DateTimeOffset.UtcNow);
        var target = available && snapshot.Target is { } entry ? new NpcTargetLookupRow(entry.Name, entry.DataId, entry.Kind,
            entry.Player is { } player ? new(player.HomeWorldId, player.HomeWorldName, player.ClassJobId,
                player.ClassJobName, player.Level, player.LodestoneSearchUrl) : null) : null;
        return new(Scope, snapshot.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture), available,
            available ? null : snapshot.UnavailableReason ?? "The target observation expired; read the selected target again.",
            NpcTargetSnapshot.Coverage, target);
    }
}
