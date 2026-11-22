using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace CompAI;

internal sealed record CraftingLogPageObservation(DateTimeOffset CapturedAtUtc, IReadOnlyDictionary<uint, bool?> Completed);

/// <summary>One answer only. Call Query on the framework thread under the caller's continuation/permission guards.</summary>
internal sealed class CraftingLogSession
{
    private readonly CraftingLogDefinition[] definitions;
    private readonly Func<IReadOnlyList<uint>, CraftingLogPageObservation>? capture;
    private readonly string? unavailableReason;
    private readonly Dictionary<uint, CraftingLogLookupRow> observed = [];
    private bool failed;

    internal CraftingLogSession(IEnumerable<CraftingLogDefinition> definitions,
        Func<IReadOnlyList<uint>, CraftingLogPageObservation> capture)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(capture);
        this.definitions = definitions.Take(CraftingLogLookup.MaxCatalog + 1).ToArray();
        if (this.definitions.Length is 0 or > CraftingLogLookup.MaxCatalog ||
            this.definitions.Any(row => row is null || row.Id == 0 || !CraftingLogLookup.ValidText(row.Name, CraftingLogLookup.MaxNameLength)) ||
            this.definitions.Select(row => row.Id).Distinct().Count() != this.definitions.Length)
            throw new ArgumentException("Crafting notebook catalog is invalid.", nameof(definitions));
        Array.Sort(this.definitions, (left, right) => left.Id.CompareTo(right.Id));
        this.capture = capture;
    }

    private CraftingLogSession(string reason)
    {
        if (!CraftingLogLookup.ValidText(reason, 256)) throw new ArgumentException("Invalid availability reason.", nameof(reason));
        this.unavailableReason = reason;
        this.definitions = [];
    }

    internal static CraftingLogSession Unavailable(string reason) => new(reason);

    internal CraftingLogLookupResult Query(JsonElement arguments)
    {
        var options = CraftingLogLookup.Parse(arguments);
        if (this.failed) throw new InvalidOperationException("Crafting-log observations changed or became unavailable; start a new question.");
        if (this.unavailableReason is not null)
            return Build(options, DateTimeOffset.UtcNow, 0, []);

        var terms = options.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = this.definitions.Where(row =>
            (options.Ids.Count == 0 || options.Ids.Contains(row.Id)) &&
            terms.All(term => row.Name.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        var selected = matches.Skip(options.Offset).Take(options.Limit).ToList();
        var estimateTime = DateTimeOffset.UtcNow;
        // Reserve a worst-case valid row before touching native state. Byte-limited
        // rows excluded here are not read or cached, and the next page starts at them.
        CraftingLogLookupRow[] Estimate() => selected.Select(row => this.observed.TryGetValue(row.Id, out var cached) ? cached :
            new CraftingLogLookupRow(row.Id, row.Name, Stamp(estimateTime), "not_observed_complete", null, new string('x', 256))).ToArray();
        while (selected.Count > 0 && JsonSerializer.SerializeToUtf8Bytes(Build(options, estimateTime, matches.Length, Estimate())).Length > CraftingLogLookup.MaxBytes)
            selected.RemoveAt(selected.Count - 1);
        if (selected.Count == 0 && options.Offset < matches.Length)
            throw new InvalidOperationException("Crafting-log result cannot fit a row inside its byte bound.");
        var missing = selected.Where(row => !this.observed.ContainsKey(row.Id)).Select(row => row.Id).ToArray();
        if (this.observed.Count + missing.Length > CraftingLogLookup.MaxCachedRows)
            throw new InvalidOperationException("This answer's crafting-log observation limit was reached; start a new question with a narrower filter.");

        try
        {
            // Even a cached-only or no-match query revalidates the session owner.
            // The empty list must not cause any completion-method calls.
            var page = this.capture!(missing);
            if (page.Completed.Count != missing.Length || missing.Any(id => !page.Completed.ContainsKey(id)))
                throw new InvalidOperationException("Crafting-log page observations are incomplete or unexpected.");
            var additions = selected.Where(row => !this.observed.ContainsKey(row.Id)).Select(row =>
            {
                var completed = page.Completed[row.Id];
                return new CraftingLogLookupRow(row.Id, row.Name, Stamp(page.CapturedAtUtc),
                    completed == true ? "completed" : completed == false ? "not_observed_complete" : "unknown",
                    completed == true ? true : null,
                    completed == true ? null : completed == false ?
                        "Native false has unverified negative-state readiness; this does not establish that the recipe has never been crafted." :
                        "No supported completion observation is available for this recipe.");
            }).ToArray();
            foreach (var row in additions) this.observed.Add(row.id, row);
            var result = Build(options, DateTimeOffset.UtcNow, matches.Length, selected.Select(row => this.observed[row.Id]).ToArray());
            if (JsonSerializer.SerializeToUtf8Bytes(result).Length > CraftingLogLookup.MaxBytes)
                throw new InvalidOperationException("Crafting-log result exceeded its byte bound.");
            return result;
        }
        catch
        {
            // Never replace an earlier available result with contradictory metadata.
            // The caller surfaces a tool error; all later calls on this answer fail.
            this.failed = true;
            throw;
        }
    }

    private CraftingLogLookupResult Build(CraftingLogQueryArguments options, DateTimeOffset queriedAt,
        int matchedCount, CraftingLogLookupRow[] rows)
    {
        var available = this.unavailableReason is null;
        int? next = available && options.Offset + rows.Length < matchedCount ? options.Offset + rows.Length : null;
        return new(CraftingLogLookup.Scope, Stamp(queriedAt), available, this.unavailableReason,
            CraftingLogLookup.Coverage, CraftingLogLookup.Source, available ? "positive_observations_only" : "unavailable",
            available ? this.definitions.Length : null, matchedCount, rows, options.Offset, options.Limit, next, next is not null);
    }

    private static string Stamp(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
