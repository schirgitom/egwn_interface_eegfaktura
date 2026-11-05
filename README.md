# EGWN EEGFaktura Interface

The solution follows the layer separation used by `EGWNInterfaceEda`:

- `EGWNInterfaceEEGFaktura`: ASP.NET Core host, scheduled sync, manual backfill endpoint, health, and metrics.
- `EGWNInterfaceEEGFaktura.Domain`: readings and EDA-compatible publication models.
- `EGWNInterfaceEEGFaktura.Application`: client, publisher, and orchestration ports and services.
- `EGWNInterfaceEEGFaktura.Infrastructure`: Keycloak, EEGFaktura, and RabbitMQ adapters.
- `EGWNInterfaceEEGFaktura.Tests`: automated tests.

## Data flow

Each EEGFaktura request sends `Authorization: Basic <base64(username:password)>` using the account's `preferred_username` (not its email address), and `X-Tenant` with the RC number. The `/api/...` endpoints use standard Base64; `/energystore/...` uses URL-safe Base64 with `=` padding retained. Passwords containing `:` are unsupported for energystore requests and trigger a startup warning. The client loads eligible meter points and their activation dates from `GET /api/master/masterdata` and posts them to `/energystore/query/rawdata` as `cps: [{ "meteringPoint": "..." }]`, with the network operator's community ID in `ecId` (distinct from the RC tenant ID). The raw-data request uses Unix timestamps in milliseconds. The scheduled job fetches the last 28 days every 180 minutes.

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

Like the EDA interface, EEGFaktura can load configuration from a JSON document in Consul KV before binding its options. Set `CONSUL_URL` (or `Consul:Address`) and optionally `CONSUL_KV_PATH` (or `Consul:KvPath`); the default path is `egwn-interface-eegfaktura/appsettings`. JSON objects and arrays are flattened into configuration keys such as `EegFaktura:Tenants:0:Password`. Consul KV overrides appsettings and environment configuration; set deployment-specific credentials in the selected Consul document if both are configured. When a Consul URL is provided, an unavailable, missing, or invalid KV document fails startup instead of silently using stale local settings.

Consul service registration is disabled in local appsettings (`Consul:Enabled=false`). To enable it, set `Consul__Enabled=true` and `CONSUL_URL` (or `Consul__Address`); the KV URL is reused for registration if no separate address is configured. `ServiceId`, `ServiceName`, `Tags`, `Ttl`, and `HeartbeatInterval` follow the EDA registration pattern. The service uses a TTL check with a 10-second heartbeat and deregisters on shutdown. As in EDA, the registration uses the machine name and port 0; it is a presence/TTL registration, not a discovery URL for the HTTP API.

For `/api/...` calls, the account also needs the `EEG_ADMIN` group. A 403 logs a diagnostic covering credentials, tenant, encoding, 2FA/OTP, Direct Access Grants, and `EEG_ADMIN`.
The scheduled sync logs the tenant before and after each run and identifies the failing tenant and `ecId` on error. A rejected raw-data request (HTTP 400) logs the tenant, `ecId`, actual URL and a response body with the configured password and Basic token redacted at Warning level; no Debug configuration is needed.

## Backfill and operations

`POST /api/readings/meter` triggers readings across all configured tenants, or selects one when `tenantId` is supplied in the JSON body. For one meter, include both `meterId` and `tenantId`, or use `POST /api/readings/meter/{meterId}` with `tenantId` in the JSON body. Both accept optional `fromUtc` and `toUtc` (for example `{ "tenantId": "RC101900", "fromUtc": "2026-10-01T00:00:00Z", "toUtc": "2026-10-02T00:00:00Z" }`); omitted dates default to the configured lookback ending now. The scheduled sync and backfill process all configured tenants. The `tenantId` only selects a configured tenant; callers cannot supply arbitrary credentials or community IDs.

`POST /api/readings/backfill` supports optional ISO-8601 `from` and `to` query parameters. If `from` is omitted, the earliest known meter activation date is used; if `to` is omitted, the current time is used. Backfill requests are split into configured chunks. `/health` reports host health and `/metrics` exposes Prometheus metrics.

In addition to HTTP and process metrics, `/metrics` exposes `eegfaktura_meter_reading_total{meter_id,tenant_id,status}`, per-meter last attempt/success timestamps, last published reading count and fetch/publish duration. A successful request with no readings counts as success with zero readings; a failed batch fetch counts as a failure for each requested meter. Backfill metrics (`eegfaktura_backfill_*`) track completed, failed and remaining tenant/time-window requests, published readings and duration; interrupted runs retain the unattempted count. `eegfaktura_job_next_fire_timestamp_seconds{job="eegfaktura-sync"}` and `eegfaktura_job_previous_fire_timestamp_seconds` report Quartz times as Unix seconds (0 when unavailable), refreshed every 10 seconds. Prometheus must scrape `/metrics`; no readings or credentials are exported.

Run with `dotnet run --project EGWNInterfaceEEGFaktura` and test with `dotnet test EGWNInterfaceEEGFaktura.slnx`.
The host requires EEGFaktura username/password settings and reachable RabbitMQ configuration; these are intentionally not included in the repository.

An opt-in live test verifies Basic authentication, meter discovery, and the read-only raw-data request. The ignored local file `EGWNInterfaceEEGFaktura.Tests/live.runsettings` contains all live-test settings. Enter `EEGFAKTURA_LIVE_USERNAME` and `EEGFAKTURA_LIVE_PASSWORD`, then set `EEGFAKTURA_LIVE_TESTS` to `true` to enable it. The test reads that file during discovery, so the live-test switch works in Visual Studio Test Explorer too; explicitly passing the settings file on the command line is optional:

```text
dotnet test EGWNInterfaceEEGFaktura.Tests --settings EGWNInterfaceEEGFaktura.Tests/live.runsettings --filter "Category=Live"
```

The live tests `RC101900_CanDiscoverMetersAndFetchRawData` and `RC103960_CanDiscoverMetersAndFetchRawData` select tenants by RC number from `EegFaktura:Tenants` in `appsettings.json` (with environment-variable overrides). Each test discovers that tenant's meters and requests the same rolling lookback window and meter list as the scheduled sync, without publishing to RabbitMQ. To run only the second tenant, use `dotnet test EGWNInterfaceEEGFaktura.Tests --settings EGWNInterfaceEEGFaktura.Tests/live.runsettings --filter "FullyQualifiedName~RC103960_CanDiscoverMetersAndFetchRawData"`. These tests fail separately if either tenant is missing or its request is rejected. Run them only when intentionally testing against EEGFaktura.

By default, the live test uses the configured EGWN tenant, community ID, sample meter, and 27 October 2026 data window from the example request. Override these with `EEGFAKTURA_LIVE_TENANT_ID`, `EEGFAKTURA_LIVE_EC_ID`, `EEGFAKTURA_LIVE_METER_ID`, `EEGFAKTURA_LIVE_START`, and `EEGFAKTURA_LIVE_END` as needed. The live test uses `preferred_username` for `EEGFAKTURA_LIVE_USERNAME`. Debug logging reports the actual request URL, request-header names and tenant, response status, headers and body; it never logs the Basic authorization value or the configured password.
