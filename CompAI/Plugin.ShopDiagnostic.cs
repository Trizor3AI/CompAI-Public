using System;
using System.Threading;
using System.Threading.Tasks;

namespace CompAI;

public sealed partial class Plugin
{
    internal async Task<ShopDiagnosticCapture> CaptureShopDiagnostic()
    {
        int contextEpoch; int accessEpoch;
        lock (this.stateGate) { contextEpoch = this.contextLoadEpoch; accessEpoch = this.valuationAccessEpoch; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            return await Framework.RunOnFrameworkThread(() =>
            {
                lock (this.stateGate)
                {
                    var valid = !deadline.IsCancellationRequested && this.ShopDiagnosticCurrent(contextEpoch, accessEpoch);
                    var result = valid ? this.shopReader.CaptureDiagnostic()
                        : new ShopDiagnosticResult(null, "Enable shop access and log into a character first.");
                    if (valid && this.ShopHighlightEnabled)
                        result = ShopDiagnosticFormatter.WithHighlight(result, this.shopHighlight.Diagnostic);
                    if (deadline.IsCancellationRequested || !this.ShopDiagnosticCurrent(contextEpoch, accessEpoch))
                        result = new ShopDiagnosticResult(null, "Character or permission changed; nothing copied.");
                    return new ShopDiagnosticCapture(contextEpoch, accessEpoch, result);
                }
            }).WaitAsync(deadline.Token);
        }
        catch (Exception)
        {
            return new(contextEpoch, accessEpoch, new ShopDiagnosticResult(null, "Shop diagnostic timed out or became unavailable; nothing copied."));
        }
    }

    internal bool CanCopyShopDiagnostic(ShopDiagnosticCapture capture)
    {
        lock (this.stateGate) return this.ShopDiagnosticCurrent(capture.ContextEpoch, capture.AccessEpoch);
    }

    private bool ShopDiagnosticCurrent(int contextEpoch, int accessEpoch) =>
        !this.contextDisposed && this.CanReadCurrentCharacter && this.Access.Shops &&
        contextEpoch == this.contextLoadEpoch && accessEpoch == this.valuationAccessEpoch;
}

internal sealed record ShopDiagnosticCapture(int ContextEpoch, int AccessEpoch, ShopDiagnosticResult Result);
