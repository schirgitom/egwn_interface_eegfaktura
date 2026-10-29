using EGWNInterfaceEEGFaktura.Domain;

namespace EGWNInterfaceEEGFaktura.Application.Abstractions;

public interface IEegFakturaClient
{
    Task<IReadOnlyList<EegMeterPoint>> GetMeterPointsAsync(string tenantId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, IReadOnlyList<EegRawReading>>> FetchRawReadingsAsync(
        string eegId,
        string tenantId,
        IReadOnlyList<string> meterIds,
        long startUnixMilliseconds,
        long endUnixMilliseconds,
        CancellationToken cancellationToken);
}
