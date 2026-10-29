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
using Microsoft.Extensions.Options;

namespace EGWNInterfaceEEGFaktura.Infrastructure.Services;

public sealed class EegFakturaClient(
    HttpClient httpClient,
    IOptions<EegFakturaOptions> options,
    ILogger<EegFakturaClient> logger) : IEegFakturaClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private const string ForbiddenMessage =
        "Login fehlgeschlagen oder Tenant nicht im Token – prüfen: Base64-Variante, X-Tenant, kein 2FA/OTP am Konto, Direct Access Grants am Keycloak-Client (Betreiber), EEG_ADMIN für /api.";
    private readonly EegFakturaOptions _options = options.Value;

    public async Task<IReadOnlyList<EegMeterPoint>> GetMeterPointsAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/participant", tenantId);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        EnsureSuccess(response);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("EEGFaktura participant response must be a JSON array.");
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
                    || string.Equals(status, "INIT", StringComparison.OrdinalIgnoreCase)
                    || !meter.TryGetProperty("participantState", out var participantState)
                    || participantState.ValueKind != JsonValueKind.Object)
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
        string eegId,
        string tenantId,
        IReadOnlyList<string> meterIds,
        long startUnixMilliseconds,
        long endUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        if (startUnixMilliseconds > endUnixMilliseconds)
        {
            throw new ArgumentException("The raw-data start must be earlier than or equal to the end.", nameof(startUnixMilliseconds));
        }

        var path = $"/energystore/eeg/v2/{Uri.EscapeDataString(eegId)}/raw";
        using var request = CreateRequest(HttpMethod.Post, path, tenantId);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new RawDataRequest(meterIds, startUnixMilliseconds, endUnixMilliseconds), SerializerOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        EnsureSuccess(response);

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
                    || reading.Value is not { Length: >= 2 })
                {
                    throw new InvalidDataException($"EEGFaktura returned an invalid reading for meter '{meterId}'.");
                }

                meterReadings.Add(new EegRawReading(
                    DateTimeOffset.FromUnixTimeMilliseconds(reading.Timestamp.Value),
                    reading.Value[0],
                    reading.Value[1]));
            }

            readings[meterId] = meterReadings;
        }

        return readings;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string tenantId)
    {
        var request = new HttpRequestMessage(method, path);
        var credentials = Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}");
        var authorization = Convert.ToBase64String(credentials);
        if (path.StartsWith("/energystore/", StringComparison.Ordinal))
        {
            authorization = authorization.Replace('+', '-').Replace('/', '_');
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorization);
        request.Headers.Add("X-Tenant", tenantId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            logger.LogWarning("{AuthenticationFailureMessage}", ForbiddenMessage);
            throw new HttpRequestException(ForbiddenMessage, null, response.StatusCode);
        }

        throw new HttpRequestException(
            $"EEGFaktura request failed with HTTP {(int)response.StatusCode}.",
            null,
            response.StatusCode);
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? GetActiveSince(JsonElement meter)
    {
        if (!meter.TryGetProperty("participantState", out var participantState)
            || participantState.ValueKind != JsonValueKind.Object
            || !participantState.TryGetProperty("activeSince", out var activeSince)
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
        IReadOnlyList<string> Meters,
        long Start,
        long End);

    private sealed class RawMeterData
    {
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
