using System.Net;
using System.Text;
using EGWNInterfaceEEGFaktura.Application.Options;
using EGWNInterfaceEEGFaktura.Application.Abstractions;
using EGWNInterfaceEEGFaktura.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EGWNInterfaceEEGFaktura.Tests;

public sealed class EegFakturaClientTests
{
    [Fact]
    public async Task GetMeterPointsAsync_UsesParticipantEndpointAndTenantHeader()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal("/api/participant", request.RequestUri?.AbsolutePath);
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
                        "participantState": { "activeSince": "2025-01-01T00:00:00Z" }
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

        var meterPoints = await client.GetMeterPointsAsync("tenant-1", CancellationToken.None);

        var meterPoint = Assert.Single(meterPoints);
        Assert.Equal("meter-active", meterPoint.MeterPointNumber);
        Assert.Equal(DateTimeOffset.Parse("2025-01-01T00:00:00Z"), meterPoint.ActiveSince);
    }

    [Fact]
    public async Task FetchRawReadingsAsync_UsesRawEndpointAndMapsResponse()
    {
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("/energystore/eeg/v2/eeg-1/raw", request.RequestUri?.AbsolutePath);
            Assert.Equal("tenant-1", request.Headers.GetValues("X-Tenant").Single());
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal("YTo-Pw==", request.Headers.Authorization?.Parameter);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Contains("\"meters\":[\"meter-1\"]", body);
            Assert.Contains("\"start\":1000", body);
            Assert.Contains("\"end\":2000", body);

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
            "eeg-1",
            "tenant-1",
            ["meter-1"],
            1000,
            2000,
            CancellationToken.None);

        var reading = Assert.Single(Assert.Single(response).Value);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1000), reading.Timestamp);
        Assert.Equal(1.25m, reading.TotalValue);
        Assert.Equal(0.4m, reading.CommunityShareValue);
    }

    [Fact]
    public async Task GetMeterPointsAsync_ReportsAuthenticationGuidanceForForbiddenResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetMeterPointsAsync("tenant-1", CancellationToken.None));

        Assert.Equal(
            "Login fehlgeschlagen oder Tenant nicht im Token – prüfen: Base64-Variante, X-Tenant, kein 2FA/OTP am Konto, Direct Access Grants am Keycloak-Client (Betreiber), EEG_ADMIN für /api.",
            exception.Message);
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    private static EegFakturaClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://eeg-faktura.at")
        };
        var options = Options.Create(new EegFakturaOptions
        {
            Username = "a",
            Password = ">?"
        });
        return new EegFakturaClient(httpClient, options, NullLogger<EegFakturaClient>.Instance);
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
}
