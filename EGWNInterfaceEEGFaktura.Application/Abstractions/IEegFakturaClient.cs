using EGWNInterfaceEEGFaktura.Application.Options;
using EGWNInterfaceEEGFaktura.Domain;

namespace EGWNInterfaceEEGFaktura.Application.Abstractions;

public interface IEegFakturaClient
{
    Task<IReadOnlyList<EegMeterPoint>> GetMeterPointsAsync(
        EegFakturaTenantOptions tenant,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, IReadOnlyList<EegRawReading>>> FetchRawReadingsAsync(
        EegFakturaTenantOptions tenant,
        IReadOnlyList<string> meterIds,
        long startUnixMilliseconds,
        long endUnixMilliseconds,
        CancellationToken cancellationToken);
}
