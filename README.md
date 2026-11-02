# EGWN EEGFaktura Interface

The solution follows the layer separation used by `EGWNInterfaceEda`:

- `EGWNInterfaceEEGFaktura`: ASP.NET Core host, scheduled sync, manual backfill endpoint, health, and metrics.
- `EGWNInterfaceEEGFaktura.Domain`: readings and EDA-compatible publication models.
- `EGWNInterfaceEEGFaktura.Application`: client, publisher, and orchestration ports and services.
- `EGWNInterfaceEEGFaktura.Infrastructure`: Keycloak, EEGFaktura, and RabbitMQ adapters.
- `EGWNInterfaceEEGFaktura.Tests`: automated tests.

## Data flow

Each EEGFaktura request sends `Authorization: Basic <base64(username:password)>` using the account's `preferred_username` (not its email address), and `X-Tenant` with the RC number. The `/api/...` endpoints use standard Base64; `/energystore/...` uses URL-safe Base64 with `=` padding retained. Passwords containing `:` are unsupported for energystore requests and trigger a startup warning. The client loads eligible meter points and their activation dates from `GET /api/master/masterdata` and posts their IDs to `/energystore/query/rawdata` with the network operator's community ID in `ecId` (distinct from the RC tenant ID). The raw-data request uses Unix timestamps in milliseconds. The scheduled job fetches the last 28 days every 180 minutes.

The response values map to the EDA-compatible publication fields according to the meter's `direction`:

- Consumption: `value[0]` (G.01) → `TotalConsumptionValue`, `value[2]` (G.03, actual EEG coverage) → `CommunityShareValue`, and `GridShareValue` = G.01 − G.03. `value[1]` (G.02) is the theoretically available EEG share, not actual coverage.
- Generation: `value[0]` (G.01, total production) → `TotalConsumptionValue` to preserve the Persister contract; `value[1]` (P.01, EVU surplus) → `GridShareValue`, and `CommunityShareValue` = G.01 − P.01.

Missing or inconsistent energy values are rejected instead of publishing readings with negative shares.

The RabbitMQ JSON envelope and field names match the EDA meter publication. `direction` and `qov` are not added to the message, so the existing Persister schema remains unchanged. RabbitMQ publishes to the `eda.sync` topic exchange with routing key `eda.sync.result`.

## Configuration and secrets

The non-secret defaults and service paths are in `EGWNInterfaceEEGFaktura/appsettings.json`. Set credentials and deployment-specific RabbitMQ values using environment variables or a secret store; never commit them:

```text
EegFaktura__Tenants__0__Username
EegFaktura__Tenants__0__Password
RabbitMq__HostName
RabbitMq__UserName
RabbitMq__Password
```

The first tenant is already present in `EegFaktura:Tenants` in `appsettings.json`. Add one object for each additional tenant; each entry needs its own `TenantId` (RC number), `EcId`, `Username` (`preferred_username`) and `Password`, with optional `CommunityName`. For example:

```json
{
  "EegFaktura": {
    "Tenants": [
      { "TenantId": "RC101900", "EcId": "AT00800000000RC101900000000000138", "CommunityName": "Energiegemeinschaft Weiz Nord" },
      { "TenantId": "RC123456", "EcId": "AT00...", "CommunityName": "Additional community" }
    ]
  }
}
```

Provide credentials via `EegFaktura__Tenants__0__Username`, `EegFaktura__Tenants__0__Password`, `EegFaktura__Tenants__1__Username`, `EegFaktura__Tenants__1__Password`, etc. When `Tenants` is nonempty, the legacy top-level `TenantId`, `EcId`, `Username` and `Password` are ignored; without it, the existing single-tenant configuration still works. Tenant IDs must be unique. `EGWNInterfaceEEGFaktura/Properties/launchSettings.json` starts the local Development profile at `http://localhost:5081/swagger`; it deliberately contains no credentials. Configure tenant and RabbitMQ credentials through environment variables or a secret store before starting the host.

For `/api/...` calls, the account also needs the `EEG_ADMIN` group. A 403 logs a diagnostic covering credentials, tenant, encoding, 2FA/OTP, Direct Access Grants, and `EEG_ADMIN`.

## Backfill and operations

`POST /api/readings/meter` triggers readings across all configured tenants, or selects one when `tenantId` is supplied in the JSON body. For one meter, include both `meterId` and `tenantId`, or use `POST /api/readings/meter/{meterId}` with `tenantId` in the JSON body. Both accept optional `fromUtc` and `toUtc` (for example `{ "tenantId": "RC101900", "fromUtc": "2026-10-01T00:00:00Z", "toUtc": "2026-10-02T00:00:00Z" }`); omitted dates default to the configured lookback ending now. The scheduled sync and backfill process all configured tenants. The `tenantId` only selects a configured tenant; callers cannot supply arbitrary credentials or community IDs.

`POST /api/readings/backfill` supports optional ISO-8601 `from` and `to` query parameters. If `from` is omitted, the earliest known meter activation date is used; if `to` is omitted, the current time is used. Backfill requests are split into configured chunks. `/health` reports host health and `/metrics` exposes Prometheus metrics.

Run with `dotnet run --project EGWNInterfaceEEGFaktura` and test with `dotnet test EGWNInterfaceEEGFaktura.slnx`.
The host requires EEGFaktura username/password settings and reachable RabbitMQ configuration; these are intentionally not included in the repository.

An opt-in live test verifies Basic authentication, meter discovery, and the read-only raw-data request. The ignored local file `EGWNInterfaceEEGFaktura.Tests/live.runsettings` contains all live-test settings. Enter `EEGFAKTURA_LIVE_USERNAME` and `EEGFAKTURA_LIVE_PASSWORD`, then set `EEGFAKTURA_LIVE_TESTS` to `true` to enable it. The test reads that file during discovery, so the live-test switch works in Visual Studio Test Explorer too; explicitly passing the settings file on the command line is optional:

```text
dotnet test EGWNInterfaceEEGFaktura.Tests --settings EGWNInterfaceEEGFaktura.Tests/live.runsettings --filter "Category=Live"
```

By default, the live test uses the configured EGWN tenant, community ID, sample meter, and 27 October 2026 data window from the example request. Override these with `EEGFAKTURA_LIVE_TENANT_ID`, `EEGFAKTURA_LIVE_EC_ID`, `EEGFAKTURA_LIVE_METER_ID`, `EEGFAKTURA_LIVE_START`, and `EEGFAKTURA_LIVE_END` as needed. The live test uses `preferred_username` for `EEGFAKTURA_LIVE_USERNAME`. Debug logging reports the actual request URL, request-header names and tenant, response status, headers and body; it never logs the Basic authorization value or the configured password.
