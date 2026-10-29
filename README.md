# EGWN EEGFaktura Interface

The solution follows the layer separation used by `EGWNInterfaceEda`:

- `EGWNInterfaceEEGFaktura`: ASP.NET Core host, scheduled sync, manual backfill endpoint, health, and metrics.
- `EGWNInterfaceEEGFaktura.Domain`: readings and EDA-compatible publication models.
- `EGWNInterfaceEEGFaktura.Application`: client, publisher, and orchestration ports and services.
- `EGWNInterfaceEEGFaktura.Infrastructure`: Keycloak, EEGFaktura, and RabbitMQ adapters.
- `EGWNInterfaceEEGFaktura.Tests`: automated tests.

## Data flow

Each EEGFaktura request sends `Authorization: Basic <base64(username:password)>` and `X-Tenant`. The `/api/...` endpoints use standard Base64; `/energystore/...` uses URL-safe Base64 with `=` padding retained. Passwords containing `:` are unsupported for energystore requests and trigger a startup warning. The client loads eligible meter points from `GET /api/participant` and posts their IDs to `/energystore/eeg/v2/{EegId}/raw`. The raw-data request uses Unix timestamps in milliseconds. The scheduled job fetches the last 28 days every 180 minutes.

The response values map to the EDA-compatible publication fields:

- `value[0]` → `TotalConsumptionValue` (also used for the total generated value for generation meters, to preserve the existing Persister contract).
- `value[1]` → `CommunityShareValue`.
- `GridShareValue` → total minus community share.

The RabbitMQ JSON envelope and field names match the EDA meter publication. `direction` and `qov` are not added to the message, so the existing Persister schema remains unchanged. RabbitMQ publishes to the `eda.sync` topic exchange with routing key `eda.sync.result`.

## Configuration and secrets

The non-secret defaults and service paths are in `EGWNInterfaceEEGFaktura/appsettings.json`. Set credentials and deployment-specific RabbitMQ values using environment variables or a secret store; never commit them:

```text
EegFaktura__Username
EegFaktura__Password
RabbitMq__HostName
RabbitMq__UserName
RabbitMq__Password
```

For `/api/...` calls, the account also needs the `EEG_ADMIN` group. A 403 logs a diagnostic covering credentials, tenant, encoding, 2FA/OTP, Direct Access Grants, and `EEG_ADMIN`.

## Backfill and operations

`POST /api/readings/backfill` supports optional ISO-8601 `from` and `to` query parameters. If `from` is omitted, the earliest known meter activation date is used; if `to` is omitted, the current time is used. Backfill requests are split into configured chunks. `/health` reports host health and `/metrics` exposes Prometheus metrics.

Run with `dotnet run --project EGWNInterfaceEEGFaktura` and test with `dotnet test EGWNInterfaceEEGFaktura.slnx`.
The host requires EEGFaktura username/password settings and reachable RabbitMQ configuration; these are intentionally not included in the repository.

An opt-in live test verifies Basic authentication, meter discovery, and the read-only raw-data request. Put `EEGFAKTURA_LIVE_USERNAME` and `EEGFAKTURA_LIVE_PASSWORD` in the ignored local file `EGWNInterfaceEEGFaktura.Tests/live.runsettings`. The test reads that file during discovery, so the live-test switch works in Visual Studio Test Explorer too; explicitly passing the settings file on the command line is optional:

```text
dotnet test EGWNInterfaceEEGFaktura.Tests --settings EGWNInterfaceEEGFaktura.Tests/live.runsettings --filter "Category=Live"
```

By default, the live test uses the configured EGWN tenant, EEG identifier, sample meter, and 27 October 2026 data window from the example request. Override these with `EEGFAKTURA_LIVE_TENANT_ID`, `EEGFAKTURA_LIVE_EEG_ID`, `EEGFAKTURA_LIVE_METER_ID`, `EEGFAKTURA_LIVE_START`, and `EEGFAKTURA_LIVE_END` as needed. The test never prints credentials or tokens.
