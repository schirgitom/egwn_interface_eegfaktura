# EGWN EEGFaktura Interface

This solution is organized after the `EGWNInterfaceEda` project:

- `EGWNInterfaceEEGFaktura`: ASP.NET Core host, configuration, logging, metrics, and health endpoint.
- `EGWNInterfaceEEGFaktura.Domain`: domain model boundary.
- `EGWNInterfaceEEGFaktura.Application`: integration ports and options.
- `EGWNInterfaceEEGFaktura.Infrastructure`: adapters for external services and RabbitMQ.
- `EGWNInterfaceEEGFaktura.Tests`: automated tests.

The application layer defines generic `IEegFakturaClient<TData>` and `IRabbitMqResultPublisher<TMessage>` ports plus a sync orchestration boundary. Concrete data models, EEGFaktura authentication and endpoints, synchronization schedule, RabbitMQ implementation, and the Persister message contract are intentionally not included until those requirements are agreed.

Configuration is loaded from `appsettings.json` and environment variables. Override the `EegFaktura` and `RabbitMq` sections with deployment-specific values; keep credentials in a secret store rather than source control. The checked-in `api.eegfaktura.example` URL is a placeholder, not a live endpoint.

Run the host with `dotnet run --project EGWNInterfaceEEGFaktura`; its health endpoint is `/health` and Prometheus metrics are exposed at `/metrics`. Build and test the solution with `dotnet build` and `dotnet test EGWNInterfaceEEGFaktura.Tests`.
