using EGWNInterfaceEEGFaktura.Application.Abstractions;
using EGWNInterfaceEEGFaktura.Application.Options;
using EGWNInterfaceEEGFaktura.Application.Services;
using EGWNInterfaceEEGFaktura.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EGWNInterfaceEEGFaktura.Tests;

public sealed class EegFakturaSyncOrchestratorTests
{
    [Fact]
    public async Task SyncAsync_FetchesTheConfiguredLookbackAndPublishesEdaShape()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var meterPoint = new EegMeterPoint("meter-1", now.AddYears(-1));
        var client = new FakeEegFakturaClient([meterPoint])
        {
            Readings = new Dictionary<string, IReadOnlyList<EegRawReading>>
            {
                ["meter-1"] = [new EegRawReading(now.AddMinutes(-15), 1.2m, 0.3m)]
            }
        };
        var publisher = new FakePublisher();
        var orchestrator = CreateOrchestrator(client, publisher, now);

        var result = await orchestrator.SyncAsync(CancellationToken.None);

        Assert.Equal(now.AddDays(-28).ToUnixTimeMilliseconds(), client.Start);
        Assert.Equal(now.ToUnixTimeMilliseconds(), client.End);
        Assert.Equal(1, result.MeterCount);
        Assert.Equal(1, result.ReadingCount);
        var publication = Assert.Single(publisher.Publications);
        Assert.Equal("meter-1", publication.MeterPointNumber);
        Assert.Equal("tenant-1", publication.CommunityId);
        var reading = Assert.Single(publication.Readings);
        Assert.Equal(1.2m, reading.TotalConsumptionValue);
        Assert.Equal(0.9m, reading.GridShareValue);
        Assert.Equal(0.3m, reading.CommunityShareValue);
    }

    [Fact]
    public async Task BackfillAsync_UsesEarliestMeterActivationAndChunksTheRange()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(14);
        var client = new FakeEegFakturaClient([new EegMeterPoint("meter-1", start)]);
        var orchestrator = CreateOrchestrator(client, new FakePublisher(), end, chunkDays: 7);

        var result = await orchestrator.BackfillAsync(null, end, CancellationToken.None);

        Assert.Equal(start, result.From);
        Assert.Equal(end, result.To);
        Assert.Equal(2, result.Requests);
        Assert.Equal(start.ToUnixTimeMilliseconds(), client.Ranges[0].Start);
        Assert.Equal(start.AddDays(7).ToUnixTimeMilliseconds(), client.Ranges[0].End);
        Assert.Equal(start.AddDays(7).AddMilliseconds(1).ToUnixTimeMilliseconds(), client.Ranges[1].Start);
    }

    private static EegFakturaSyncOrchestrator CreateOrchestrator(
        FakeEegFakturaClient client,
        FakePublisher publisher,
        DateTimeOffset now,
        int chunkDays = 7) =>
        new(
            client,
            publisher,
            Options.Create(new EegFakturaOptions
            {
                TenantId = "tenant-1",
                EegId = "eeg-1",
                CommunityName = "Community",
                LookbackDays = 28,
                BackfillChunkDays = chunkDays
            }),
            new FixedTimeProvider(now),
            NullLogger<EegFakturaSyncOrchestrator>.Instance);

    private sealed class FakeEegFakturaClient(IReadOnlyList<EegMeterPoint> meterPoints) : IEegFakturaClient
    {
        public IReadOnlyDictionary<string, IReadOnlyList<EegRawReading>> Readings { get; init; } =
            new Dictionary<string, IReadOnlyList<EegRawReading>>();

        public long Start { get; private set; }

        public long End { get; private set; }

        public List<(long Start, long End)> Ranges { get; } = [];

        public Task<IReadOnlyList<EegMeterPoint>> GetMeterPointsAsync(
            string tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(meterPoints);

        public Task<IReadOnlyDictionary<string, IReadOnlyList<EegRawReading>>> FetchRawReadingsAsync(
            string eegId,
            string tenantId,
            IReadOnlyList<string> meterIds,
            long startUnixMilliseconds,
            long endUnixMilliseconds,
            CancellationToken cancellationToken)
        {
            Start = startUnixMilliseconds;
            End = endUnixMilliseconds;
            Ranges.Add((startUnixMilliseconds, endUnixMilliseconds));
            return Task.FromResult(Readings);
        }
    }

    private sealed class FakePublisher : IEegFakturaResultPublisher
    {
        public List<EegMeterReadingsPublication> Publications { get; } = [];

        public Task PublishAsync(EegMeterReadingsPublication publication, CancellationToken cancellationToken)
        {
            Publications.Add(publication);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
