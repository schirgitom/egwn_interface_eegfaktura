using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EGWNInterfaceEEGFaktura.Application.Abstractions;
using EGWNInterfaceEEGFaktura.Application.Options;
using EGWNInterfaceEEGFaktura.Domain;
using Microsoft.Extensions.Logging;

namespace EGWNInterfaceEEGFaktura.Infrastructure.Services;

public sealed class EegFakturaClient(
    HttpClient httpClient,
    ILogger<EegFakturaClient> logger) : IEegFakturaClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private const string ForbiddenMessage =
        "Login fehlgeschlagen oder Tenant nicht im Token – prüfen: Base64-Variante, X-Tenant, kein 2FA/OTP am Konto, Direct Access Grants am Keycloak-Client (Betreiber), EEG_ADMIN für /api.";

    public async Task<IReadOnlyList<EegMeterPoint>> GetMeterPointsAsync(
        EegFakturaTenantOptions tenant,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/master/masterdata", tenant);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await LogResponseAsync(response, request, tenant, cancellationToken);
        await EnsureSuccessAsync(response, request, tenant, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("EEGFaktura masterdata response must be a JSON array.");
        }

        var meterPoints = new Dictionary<string, EegMeterPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var participant in document.RootElement.EnumerateArray())
        {
            if (!participant.TryGetProperty("meters", out var meters)
                || meters.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var meter in meters.EnumerateArray())
            {
                var meterId = GetString(meter, "meteringPoint");
                var status = GetString(meter, "status");
                if (string.IsNullOrWhiteSpace(meterId)
                    || string.Equals(status, "INIT", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var activeSince = GetActiveSince(meter);
                if (!meterPoints.TryGetValue(meterId, out var existing)
                    || activeSince.HasValue && (!existing.ActiveSince.HasValue || activeSince < existing.ActiveSince))
                {
                    meterPoints[meterId] = new EegMeterPoint(meterId, activeSince);
                }
            }
        }

        return meterPoints.Values.OrderBy(meterPoint => meterPoint.MeterPointNumber).ToArray();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<EegRawReading>>> FetchRawReadingsAsync(
        EegFakturaTenantOptions tenant,
        IReadOnlyList<string> meterIds,
        long startUnixMilliseconds,
        long endUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        if (startUnixMilliseconds > endUnixMilliseconds)
        {
            throw new ArgumentException("The raw-data start must be earlier than or equal to the end.", nameof(startUnixMilliseconds));
        }

        using var request = CreateRequest(HttpMethod.Post, "energystore/query/rawdata", tenant);
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new RawDataRequest(
                    tenant.EcId,
                    meterIds.Select(meterId => new RawDataTarget(meterId)).ToArray(),
                    startUnixMilliseconds,
                    endUnixMilliseconds),
                SerializerOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await LogResponseAsync(response, request, tenant, cancellationToken);
        await EnsureSuccessAsync(response, request, tenant, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var rawResponse = await JsonSerializer.DeserializeAsync<Dictionary<string, RawMeterData>>(
            stream,
            SerializerOptions,
            cancellationToken);
        if (rawResponse is null)
        {
            throw new InvalidDataException("EEGFaktura raw-data response was empty.");
        }

        var readings = new Dictionary<string, IReadOnlyList<EegRawReading>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (meterId, meterData) in rawResponse)
        {
            if (meterData is null)
            {
                throw new InvalidDataException($"EEGFaktura returned an empty data object for meter '{meterId}'.");
            }

            var meterReadings = new List<EegRawReading>();
            foreach (var reading in meterData.Data ?? [])
            {
                if (reading is null
                    || !reading.Timestamp.HasValue
                    || reading.Value is not { Length: >= 2 } values
                    || values[0] is not { } total
                    || total < 0)
                {
                    throw new InvalidDataException($"EEGFaktura returned an invalid reading for meter '{meterId}'.");
                }

                decimal communityShare;
                if (string.Equals(meterData.Direction, "CONSUMPTION", StringComparison.OrdinalIgnoreCase)
                    && values is { Length: >= 3 }
                    && values[2] is { } utilization)
                {
                    communityShare = utilization;
                }
                else if (string.Equals(meterData.Direction, "GENERATION", StringComparison.OrdinalIgnoreCase)
                         && values[1] is { } surplus)
                {
                    communityShare = total - surplus;
                }
                else
                {
                    throw new InvalidDataException(
                        $"EEGFaktura returned an invalid {meterData.Direction ?? "missing"} reading for meter '{meterId}'.");
                }

                if (communityShare < 0 || communityShare > total)
                {
                    throw new InvalidDataException(
                        $"EEGFaktura returned an out-of-range EEG share for meter '{meterId}'.");
                }

                meterReadings.Add(new EegRawReading(
                    DateTimeOffset.FromUnixTimeMilliseconds(reading.Timestamp.Value),
                    total,
                    communityShare));
            }

            readings[meterId] = meterReadings;
        }

        return readings;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, EegFakturaTenantOptions tenant)
    {
        var request = new HttpRequestMessage(method, path);
        var credentials = Encoding.UTF8.GetBytes($"{tenant.Username}:{tenant.Password}");
        var authorization = Convert.ToBase64String(credentials);
        if (path.StartsWith("energystore/", StringComparison.Ordinal))
        {
            authorization = authorization.Replace('+', '-').Replace('/', '_');
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorization);
        request.Headers.Add("X-Tenant", tenant.TenantId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task LogResponseAsync(
        HttpResponseMessage response,
        HttpRequestMessage sentRequest,
        EegFakturaTenantOptions tenant,
        CancellationToken cancellationToken)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var request = response.RequestMessage ?? sentRequest;
        var headers = string.Join(", ", request.Headers.Select(header => header.Key));
        var tenantHeader = request.Headers.TryGetValues("X-Tenant", out var values)
            ? string.Join(", ", values)
            : null;
        var responseHeaders = string.Join(", ", response.Headers.Concat(response.Content.Headers).Select(header =>
            header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
            || header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || header.Key.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
                ? $"{header.Key}: [REDACTED]"
                : $"{header.Key}: {string.Join(", ", header.Value)}"));
        var body = Redact(await response.Content.ReadAsStringAsync(cancellationToken), tenant);
        responseHeaders = Redact(responseHeaders, tenant);

        logger.LogDebug(
            "EEGFaktura response: URL {RequestUri}, request header names {RequestHeaders}, X-Tenant {TenantId}, status {StatusCode}, response headers {ResponseHeaders}, body {ResponseBody}",
            request.RequestUri,
            headers,
            tenantHeader,
            (int)response.StatusCode,
            responseHeaders,
            body);
    }

    private async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        HttpRequestMessage sentRequest,
        EegFakturaTenantOptions tenant,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var body = Redact(await response.Content.ReadAsStringAsync(cancellationToken), tenant);
            logger.LogWarning(
                "EEGFaktura request rejected: tenant {TenantId}, ecId {EcId}, URL {RequestUri}, HTTP {StatusCode}, response body {ResponseBody}",
                tenant.TenantId,
                tenant.EcId,
                (response.RequestMessage ?? sentRequest).RequestUri,
                (int)response.StatusCode,
                body);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            logger.LogWarning("EEGFaktura tenant {TenantId}: {AuthenticationFailureMessage}", tenant.TenantId, ForbiddenMessage);
            throw new HttpRequestException(ForbiddenMessage, null, response.StatusCode);
        }

        throw new HttpRequestException(
            $"EEGFaktura request for tenant '{tenant.TenantId}' failed with HTTP {(int)response.StatusCode}.",
            null,
            response.StatusCode);
    }

    private static string Redact(string value, EegFakturaTenantOptions tenant)
    {
        if (string.IsNullOrEmpty(tenant.Password))
        {
            return value;
        }

        var basicToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{tenant.Username}:{tenant.Password}"));
        foreach (var secret in new[] { tenant.Password, basicToken, basicToken.Replace('+', '-').Replace('/', '_') }
                     .Distinct(StringComparer.Ordinal))
        {
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        return value;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? GetActiveSince(JsonElement meter)
    {
        if (!meter.TryGetProperty("activeSince", out var activeSince)
            || activeSince.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            activeSince.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private sealed record RawDataRequest(
        string EcId,
        IReadOnlyList<RawDataTarget> Cps,
        long Start,
        long End);

    private sealed record RawDataTarget(string MeteringPoint);

    private sealed class RawMeterData
    {
        [JsonPropertyName("direction")]
        public string? Direction { get; init; }

        [JsonPropertyName("data")]
        public RawReading?[]? Data { get; init; }
    }

    private sealed class RawReading
    {
        [JsonPropertyName("ts")]
        public long? Timestamp { get; init; }

        [JsonPropertyName("value")]
        public decimal?[]? Value { get; init; }
    }
}
