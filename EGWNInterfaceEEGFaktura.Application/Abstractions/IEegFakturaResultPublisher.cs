using EGWNInterfaceEEGFaktura.Domain;

namespace EGWNInterfaceEEGFaktura.Application.Abstractions;

public interface IEegFakturaResultPublisher
{
    Task PublishAsync(EegMeterReadingsPublication publication, CancellationToken cancellationToken);
}
