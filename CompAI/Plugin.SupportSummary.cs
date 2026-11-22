using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Plugin;

namespace CompAI;

public sealed partial class Plugin
{
    // Human-initiated clipboard export only. Never registered with an AI tool or request payload.
    // Select booleans and application-owned keys; do not serialize CompanionView, raw errors,
    // reader details, account labels, paths, character names or identifiers.
    internal string SupportSummary()
    {
        lock (this.stateGate)
        {
            var connection = this.CompanionView;
            return JsonSerializer.Serialize(new
            {
                diagnostic = "compai_support_v1",
                plugin_version = typeof(Plugin).Assembly.GetName().Version?.ToString(3),
                dalamud_version = typeof(IDalamudPluginInterface).Assembly.GetName().Version?.ToString(),
                runtime = RuntimeInformation.FrameworkDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                character_loaded = this.CanReadCurrentCharacter,
                cache_loading = this.contextCacheLoading,
                signed_in_reported = connection.IsSignedIn,
                account_operation_busy = connection.Busy,
                active_requests = connection.ActiveRequestCount,
                sending_blocked = connection.SendBlockReason is not null,
                chat_storage_error = connection.StorageError is not null,
                response_limit_minutes = this.ResponseLimitMinutes,
                access_save_error = this.AccessSaveError is not null,
                readers = this.ReaderStatuses().OrderBy(row => row.Key, StringComparer.Ordinal)
                    .Select(row => new { reader = row.Key, state = row.Value.State }).ToArray(),
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
