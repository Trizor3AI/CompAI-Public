using System;
using System.Threading;

namespace CompAI;

internal sealed partial class CompanionConnection
{
    // Called only while a permitted local inventory callback is executing.
    internal void AttachInventoryFindKey(Guid key)
    {
        lock (this.gate)
        {
            var operation = this.callbackOperation.Value;
            if (this.disposed || operation is null || operation.action != "ask" ||
                operation.cancelRequested || operation.questionInvalidated || !operation.busy)
                throw new OperationCanceledException();
            operation.inventoryFindKey = key;
        }
    }

    // This handle remains on the callback's owning answer, even if another chat is selected.
    internal void AttachShopFindKey(Guid key)
    {
        lock (this.gate)
        {
            var operation = this.callbackOperation.Value;
            if (this.disposed || operation is null || operation.action != "ask" ||
                operation.cancelRequested || operation.questionInvalidated || !operation.busy)
                throw new OperationCanceledException();
            operation.shopFindKey = key;
        }
    }
}
