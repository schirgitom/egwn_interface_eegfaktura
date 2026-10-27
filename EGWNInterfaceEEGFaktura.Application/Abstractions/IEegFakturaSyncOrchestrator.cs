namespace EGWNInterfaceEEGFaktura.Application.Abstractions;

public interface IEegFakturaSyncOrchestrator
{
    Task SyncAsync(CancellationToken cancellationToken);
}
