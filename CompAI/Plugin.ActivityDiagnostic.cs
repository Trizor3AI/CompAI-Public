using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CompAI;

public sealed partial class Plugin
{
    internal async Task<ActivityDiagnosticCapture> CaptureActivityDiagnostic(ActivityDiagnosticTarget target)
    {
        int contextEpoch; ulong owner;
        lock (this.stateGate) { contextEpoch = this.contextLoadEpoch; owner = this.activeContentId; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            return await Framework.RunOnFrameworkThread(() =>
            {
                lock (this.stateGate)
                {
                    var valid = !deadline.IsCancellationRequested && this.ActivityDiagnosticCurrent(contextEpoch, owner) &&
                        ReadLoadedCharacterId() == owner;
                    object? readerStatus = valid ? target switch
                    {
                        ActivityDiagnosticTarget.FreeCompany => this.freeCompanyReader.CaptureReadDiagnostic(),
                        ActivityDiagnosticTarget.GoldSaucer => new
                        {
                            category = "mahjong", access_enabled = this.Access.Mahjong,
                            retained_observation_available = this.HasRecentActivityUnderLock("mahjong"),
                            capture = this.mahjongReader.CaptureReadDiagnostic(),
                        },
                        ActivityDiagnosticTarget.Description => new
                        {
                            category = "portable_archive", access_enabled = this.Access.CollectionTools,
                            retained_observation_available = this.HasRecentActivityUnderLock("portable_archive"),
                            capture = this.portableArchiveReader.CaptureReadDiagnostic(),
                        },
                        ActivityDiagnosticTarget.CosmicMissions => this.CaptureCosmicMissionDiagnostic(),
                        _ => null,
                    } : null;
                    var result = valid ? ActivityMenuDiagnostic.Capture(GameGui, target, readerStatus)
                        : new ChallengeDiagnosticResult(null, "Log into a loaded character first; nothing copied.");
                    // A failed menu guard is itself useful diagnostic evidence. This fallback
                    // contains fixed reader states only, never text from an excluded menu tab.
                    if (readerStatus is not null && result.Text is null)
                    {
                        var statusOnly = JsonSerializer.Serialize(new
                        {
                            diagnostic = target == ActivityDiagnosticTarget.FreeCompany ? "compai_fc_reader_status_v1" : "compai_activity_reader_status_v1",
                            selected_menu = ActivityMenuDiagnostic.Labels[(int)target], reader_status = readerStatus,
                        });
                        if (Encoding.UTF8.GetByteCount(statusOnly) <= ActivityMenuDiagnostic.MaxOutputBytes)
                            result = new(statusOnly, "Reader status copied; menu text unavailable. Open the selected menu tab to include it.");
                    }
                    if (deadline.IsCancellationRequested || !this.ActivityDiagnosticCurrent(contextEpoch, owner) ||
                        ReadLoadedCharacterId() != owner)
                        result = new(null, "Character changed or diagnostic expired; nothing copied.");
                    return new ActivityDiagnosticCapture(contextEpoch, owner, result);
                }
            }).WaitAsync(deadline.Token);
        }
        catch (Exception)
        {
            return new(contextEpoch, owner, new(null, "Menu diagnostic timed out or became unavailable; nothing copied."));
        }
    }

    internal bool CanCopyActivityDiagnostic(ActivityDiagnosticCapture capture)
    {
        lock (this.stateGate) return this.ActivityDiagnosticCurrent(capture.ContextEpoch, capture.Owner);
    }

    private object CaptureCosmicMissionDiagnostic()
    {
        // Explicit troubleshooting only; this does not populate the AI's retained observation.
        _ = this.cosmicMissionReader.Capture(ClientState.TerritoryType);
        return new
        {
            category = "cosmic_missions", access_enabled = this.Access.CosmicMissions,
            retained_observation_available = this.HasRecentActivityUnderLock("cosmic_missions"),
            capture = this.cosmicMissionReader.LastCaptureStatus,
        };
    }

    // A manual human diagnostic intentionally does not grant or require an AI reader permission.
    private bool ActivityDiagnosticCurrent(int contextEpoch, ulong owner) =>
        !this.contextDisposed && this.CanReadCurrentCharacter && owner != 0 && owner == this.activeContentId &&
        contextEpoch == this.contextLoadEpoch;
}

internal sealed record ActivityDiagnosticCapture(int ContextEpoch, ulong Owner, ChallengeDiagnosticResult Result);
