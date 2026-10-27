namespace EGWNInterfaceEEGFaktura.Application.Abstractions;

public interface IEegFakturaClient<TData>
{
    Task<TData> FetchAsync(CancellationToken cancellationToken);
}
