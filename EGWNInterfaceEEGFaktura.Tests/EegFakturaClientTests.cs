using System.Net;
using System.Text;
using System.Text.Json;
using EGWNInterfaceEEGFaktura.Application.Options;
using EGWNInterfaceEEGFaktura.Application.Abstractions;
using EGWNInterfaceEEGFaktura.Application.Services;
using EGWNInterfaceEEGFaktura.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EGWNInterfaceEEGFaktura.Tests;

public sealed class EegFakturaClientTests
{
    private static readonly EegFakturaTenantOptions Tenant = new()
    {
        TenantId = "tenant-1",
        EcId = "AT00800000000RC101900000000000138",
        Username = "a",
        Password = ">?"
    };

    [Fact]
    public async Task GetMeterPointsAsync_UsesMasterdataEndpointAndTenantHeader()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal("https://eeg-faktura.at/api/master/masterdata", request.RequestUri?.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("tenant-1", request.Headers.GetValues("X-Tenant").Single());
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal("YTo+Pw==", request.Headers.Authorization?.Parameter);

            return Task.FromResult(JsonResponse(
                """
                [
                  {
                    "meters": [
                      {
                        "meteringPoint": "meter-active",
                        "status": "ACTIVE",
                        "activeSince": "2025-01-01T00:00:00Z"
                      },
                      {
                        "meteringPoint": "meter-init",
                        "status": "INIT"
                      }
                    ]
                  }
                ]
                """));
        });
        var client = CreateClient(handler);

        var meterPoints = await client.GetMeterPointsAsync(Tenant, CancellationToken.None);

        var meterPoint = Assert.Single(meterPoints);
        Assert.Equal("meter-active", meterPoint.MeterPointNumber);
        Assert.Equal(DateTimeOffset.Parse("2025-01-01T00:00:00Z"), meterPoint.ActiveSince);
    }

    [Fact]
    public async Task FetchRawReadingsAsync_UsesRawEndpointAndMapsResponse()
    {
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("https://eeg-faktura.at/energystore/query/rawdata", request.RequestUri?.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("tenant-1", request.Headers.GetValues("X-Tenant").Single());
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal("YTo-Pw==", request.Headers.Authorization?.Parameter);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var payload = document.RootElement;
            Assert.Equal(4, payload.EnumerateObject().Count());
            Assert.Equal(Tenant.EcId, payload.GetProperty("ecId").GetString());
            Assert.Equal(1000, payload.GetProperty("start").GetInt64());
            Assert.Equal(2000, payload.GetProperty("end").GetInt64());
            var targets = payload.GetProperty("cps").EnumerateArray().ToArray();
            Assert.Equal(2, targets.Length);
            Assert.Equal("meter-1", targets[0].GetProperty("meteringPoint").GetString());
            Assert.Equal("meter-2", targets[1].GetProperty("meteringPoint").GetString());
            Assert.All(targets, target => Assert.Single(target.EnumerateObject()));

            return JsonResponse(
                """
                {
                  "meter-1": {
                    "data": [
                      { "ts": 1000, "value": [1.25, 0.4], "qov": [1, 3] }
                    ],
                    "direction": "GENERATION"
                  }
                }
                """);
        });
        var client = CreateClient(handler);

        var response = await client.FetchRawReadingsAsync(
            Tenant,
            ["meter-1", "meter-2"],
            1000,
            2000,
            CancellationToken.None);

        var reading = Assert.Single(Assert.Single(response).Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1000), reading.Timestamp);
        Assert.Equal(1.25m, reading.TotalValue);
        Assert.Equal(0.85m, reading.CommunityShareValue);
        Assert.Equal(0.4m, EegReadingMapper.Map(reading).GridShareValue);
    }

    [Fact]
    public async Task FetchRawReadingsAsync_UsesActualEegCoverageInsteadOfTheoreticalAllocation()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(
            """
            {
              "meter-1": {
                "direction": "CONSUMPTION",
                "data": [
                  { "ts": 1000, "value": [0.007, 0.008083, 0.007] },
                  { "ts": 2000, "value": [0.103, 0.000142, 0.000142] }
                ]
              }
            }
            """)));
        var client = CreateClient(handler);

        var response = await client.FetchRawReadingsAsync(Tenant, ["meter-1"], 1000, 2000, CancellationToken.None);

        var readings = Assert.Single(response).Value;
        Assert.Equal(0.007m, readings[0].TotalValue);
        Assert.Equal(0.007m, readings[0].CommunityShareValue);
        var mapped = EegReadingMapper.Map(readings[0]);
        Assert.Equal(0m, mapped.GridShareValue);
        Assert.InRange(mapped.GridShareValue!.Value / mapped.TotalConsumptionValue!.Value, 0m, 1m);
        Assert.InRange(mapped.CommunityShareValue!.Value / mapped.TotalConsumptionValue.Value, 0m, 1m);
        Assert.Equal(0.000142m, readings[1].CommunityShareValue);
    }

    [Theory]
    [InlineData("""{"meter-1":{"direction":"CONSUMPTION","data":[{"ts":1000,"value":[1,2]}]}}""")]
    [InlineData("""{"meter-1":{"direction":"CONSUMPTION","data":[{"ts":1000,"value":[1,2,1.1]}]}}""")]
    [InlineData("""{"meter-1":{"direction":"GENERATION","data":[{"ts":1000,"value":[1,1.1]}]}}""")]
    [InlineData("""{"meter-1":{"direction":"OTHER","data":[{"ts":1000,"value":[1,0.5,0.5]}]}}""")]
    [InlineData("""{"meter-1":{"data":[{"ts":1000,"value":[1,0.5,0.5]}]}}""")]
    public async Task FetchRawReadingsAsync_RejectsInvalidSharesOrDirection(string json)
    {
        var client = CreateClient(new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(json))));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.FetchRawReadingsAsync(Tenant, ["meter-1"], 1000, 2000, CancellationToken.None));
    }

    [Fact]
    public async Task GetMeterPointsAsync_ReportsAuthenticationGuidanceForForbiddenResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetMeterPointsAsync(Tenant, CancellationToken.None));

        Assert.Equal(
            "Login fehlgeschlagen oder Tenant nicht im Token – prüfen: Base64-Variante, X-Tenant, kein 2FA/OTP am Konto, Direct Access Grants am Keycloak-Client (Betreiber), EEG_ADMIN für /api.",
            exception.Message);
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task GetMeterPointsAsync_LogsResponseDetailsWithoutCredentials()
    {
        var logger = new RecordingLogger();
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("rejected >? token YTo+Pw==")
            };
            response.Headers.TryAddWithoutValidation("X-Trace", "trace-1");
            response.Headers.TryAddWithoutValidation("X-Diagnostic", "YTo+Pw==");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "session=secret");
            return Task.FromResult(response);
        });
        var client = CreateClient(handler, logger);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetMeterPointsAsync(Tenant, CancellationToken.None));

        var message = Assert.Single(logger.Messages);
        Assert.Contains("https://eeg-faktura.at/api/master/masterdata", message);
        Assert.Contains("Authorization", message);
        Assert.Contains("X-Tenant tenant-1", message);
        Assert.Contains("status 403", message);
        Assert.Contains("X-Trace: trace-1", message);
        Assert.Contains("body rejected [REDACTED] token [REDACTED]", message);
        Assert.DoesNotContain("session=secret", message);
        Assert.DoesNotContain(">?", message);
        Assert.DoesNotContain("YTo+Pw==", message);
    }

    [Fact]
    public async Task GetMeterPointsAsync_UsesCredentialsForEachTenant()
    {
        var headers = new List<(string TenantId, string? Authorization)>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            headers.Add((request.Headers.GetValues("X-Tenant").Single(), request.Headers.Authorization?.Parameter));
            return Task.FromResult(JsonResponse("[]"));
        });
        var client = CreateClient(handler);

        await client.GetMeterPointsAsync(Tenant, CancellationToken.None);
        await client.GetMeterPointsAsync(new EegFakturaTenantOptions
        {
            TenantId = "tenant-2",
            EcId = "ec-2",
            Username = "second",
            Password = "different"
        }, CancellationToken.None);

        Assert.Equal("tenant-1", headers[0].TenantId);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("a:>?")), headers[0].Authorization);
        Assert.Equal("tenant-2", headers[1].TenantId);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("second:different")), headers[1].Authorization);
    }

    [Fact]
    public async Task FetchRawReadingsAsync_LogsBadRequestWithTenantAndRedactedResponseAtWarningLevel()
    {
        var logger = new RecordingLogger(LogLevel.Warning);
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("Invalid ecId for >? (YTo-Pw==)")
            }));
        var client = CreateClient(handler, logger);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.FetchRawReadingsAsync(Tenant, ["meter-1"], 1000, 2000, CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("tenant-1", exception.Message);
        var message = Assert.Single(logger.Messages);
        Assert.Contains("tenant tenant-1", message);
        Assert.Contains("ecId AT00800000000RC101900000000000138", message);
        Assert.Contains("https://eeg-faktura.at/energystore/query/rawdata", message);
        Assert.Contains("HTTP 400", message);
        Assert.Contains("Invalid ecId for [REDACTED] ([REDACTED])", message);
        Assert.DoesNotContain(">?", message);
        Assert.DoesNotContain("YTo-Pw==", message);
    }

    private static EegFakturaClient CreateClient(
        HttpMessageHandler handler,
        ILogger<EegFakturaClient>? logger = null)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://eeg-faktura.at/")
        };
        return new EegFakturaClient(httpClient, logger ?? NullLogger<EegFakturaClient>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class RecordingLogger(LogLevel enabledLevel = LogLevel.Debug) : ILogger<EegFakturaClient>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel == enabledLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Messages.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose() { }
        }
    }
}
