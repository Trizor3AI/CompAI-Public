using System;
using System.Threading;
using System.Threading.Tasks;

namespace CompAI;

public sealed partial class Plugin
{
    internal async Task<ChallengeDiagnosticCapture> CaptureChallengeDiagnostic()
    {
        int contextEpoch; int accessEpoch;
        lock (this.stateGate) { contextEpoch = this.contextLoadEpoch; accessEpoch = this.progressionAccessEpoch; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            return await Framework.RunOnFrameworkThread(() =>
            {
                lock (this.stateGate)
                {
                    var valid = !deadline.IsCancellationRequested && this.ChallengeDiagnosticCurrent(contextEpoch, accessEpoch);
                    var result = valid ? ChallengeLogDiagnostic.Capture(GameGui)
                        : ChallengeLogDiagnostic.Failed("Enable Challenge Log access and log into a character first.");
                    if (!this.ChallengeDiagnosticCurrent(contextEpoch, accessEpoch))
                        result = ChallengeLogDiagnostic.Failed("Character or permission changed; nothing copied.");
                    return new ChallengeDiagnosticCapture(contextEpoch, accessEpoch, result);
                }
            }).WaitAsync(deadline.Token);
        }
        catch (Exception)
        {
            return new(contextEpoch, accessEpoch, ChallengeLogDiagnostic.Failed("Challenge Log diagnostic timed out or became unavailable; nothing copied."));
        }
    }

    internal bool CanCopyChallengeDiagnostic(ChallengeDiagnosticCapture capture)
    {
        lock (this.stateGate) return this.ChallengeDiagnosticCurrent(capture.ContextEpoch, capture.AccessEpoch);
    }

    private bool ChallengeDiagnosticCurrent(int contextEpoch, int accessEpoch) =>
        !this.contextDisposed && this.CanReadCurrentCharacter && this.Access.ChallengeLog &&
        contextEpoch == this.contextLoadEpoch && accessEpoch == this.progressionAccessEpoch;
}

internal sealed record ChallengeDiagnosticCapture(int ContextEpoch, int AccessEpoch, ChallengeDiagnosticResult Result);
