using EGWNInterfaceEEGFaktura.Domain;

namespace EGWNInterfaceEEGFaktura.Application.Abstractions;

public interface IEegFakturaSyncOrchestrator
{
    Task<SyncResult> SyncAsync(CancellationToken cancellationToken);

    Task<BackfillResult> BackfillAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken);
}
