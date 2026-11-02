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

    [Fact]
    public async Task TriggerMeterReadingAsync_FetchesAllMetersForConfiguredLookback()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var client = new FakeEegFakturaClient([new EegMeterPoint("meter-1", null), new EegMeterPoint("meter-2", null)])
        {
            Readings = new Dictionary<string, IReadOnlyList<EegRawReading>>
            {
                ["meter-1"] = [new EegRawReading(now, 1.2m, 0.3m)],
                ["meter-2"] = [new EegRawReading(now, 2.2m, 0.4m)]
            }
        };
        var publisher = new FakePublisher();
        var orchestrator = CreateOrchestrator(client, publisher, now);

        var result = await orchestrator.TriggerMeterReadingAsync(new MeterReadingTriggerRequest(null, null, null), CancellationToken.None);

        Assert.Null(result.MeterId);
        Assert.Equal(now.AddDays(-28), result.FromUtc);
        Assert.Equal(now, result.ToUtc);
        Assert.Equal(2, result.ReadingCount);
        Assert.Equal(1, client.MeterDiscoveryCalls);
        Assert.Equal(["meter-1", "meter-2"], client.MeterIds);
        Assert.Equal(["meter-1", "meter-2"], publisher.Publications.Select(publication => publication.MeterPointNumber));
    }

    [Fact]
    public async Task TriggerMeterReadingAsync_FetchesOnlyRequestedMeterWithoutDiscovery()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var from = now.AddDays(-2);
        var client = new FakeEegFakturaClient([])
        {
            Readings = new Dictionary<string, IReadOnlyList<EegRawReading>>
            {
                ["meter-1"] = [new EegRawReading(now, 1.2m, 0.3m)]
            }
        };
        var publisher = new FakePublisher();
        var orchestrator = CreateOrchestrator(client, publisher, now);

        var result = await orchestrator.TriggerMeterReadingAsync(
            new MeterReadingTriggerRequest("meter-1", from, now, "tenant-1"), CancellationToken.None);

        Assert.Equal("meter-1", result.MeterId);
        Assert.Equal(from, result.FromUtc);
        Assert.Equal(now, result.ToUtc);
        Assert.Equal(1, result.ReadingCount);
        Assert.Equal(0, client.MeterDiscoveryCalls);
        Assert.Equal(["meter-1"], client.MeterIds);
        Assert.Equal(from.ToUnixTimeMilliseconds(), client.Start);
        Assert.Equal(now.ToUnixTimeMilliseconds(), client.End);
        Assert.Equal("tenant-1", Assert.Single(publisher.Publications).CommunityId);
    }

    [Fact]
    public async Task TriggerMeterReadingAsync_RejectsReversedEffectiveRange()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var client = new FakeEegFakturaClient([]);
        var orchestrator = CreateOrchestrator(client, new FakePublisher(), now);

        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.TriggerMeterReadingAsync(
            new MeterReadingTriggerRequest(null, now.AddDays(1), null), CancellationToken.None));

        Assert.Equal(0, client.MeterDiscoveryCalls);
    }

    [Fact]
    public async Task SyncAsync_PublishesEachTenantWithItsOwnConfiguration()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var client = new FakeEegFakturaClient([])
        {
            MeterPointsByTenant = new Dictionary<string, IReadOnlyList<EegMeterPoint>>
            {
                ["tenant-1"] = [new EegMeterPoint("meter-1", null)],
                ["tenant-2"] = [new EegMeterPoint("meter-2", null)]
            },
            ReadingsByTenant = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<EegRawReading>>>
            {
                ["tenant-1"] = new Dictionary<string, IReadOnlyList<EegRawReading>>
                {
                    ["meter-1"] = [new EegRawReading(now, 1m, 0.2m)]
                },
                ["tenant-2"] = new Dictionary<string, IReadOnlyList<EegRawReading>>
                {
                    ["meter-2"] = [new EegRawReading(now, 2m, 0.5m)]
                }
            }
        };
        var publisher = new FakePublisher();
        var orchestrator = CreateOrchestrator(client, publisher, now, tenants: TwoTenants());

        var result = await orchestrator.SyncAsync(CancellationToken.None);

        Assert.Equal(2, result.MeterCount);
        Assert.Equal(2, result.ReadingCount);
        Assert.Equal(["tenant-1", "tenant-2"], client.DiscoveredTenants);
        Assert.Equal(["ec-1", "ec-2"], client.FetchedTenants.Select(tenant => tenant.EcId));
        Assert.Equal(["username-1", "username-2"], client.FetchedTenants.Select(tenant => tenant.Username));
        Assert.Equal(["tenant-1", "tenant-2"], publisher.Publications.Select(publication => publication.CommunityId));
        Assert.Equal(["Community 1", "Community 2"], publisher.Publications.Select(publication => publication.CommunityName));
    }

    [Fact]
    public async Task TriggerMeterReadingAsync_TargetsOnlySelectedTenantForSingleMeter()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var client = new FakeEegFakturaClient([])
        {
            ReadingsByTenant = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<EegRawReading>>>
            {
                ["tenant-2"] = new Dictionary<string, IReadOnlyList<EegRawReading>>
                {
                    ["meter-1"] = [new EegRawReading(now, 2m, 0.5m)]
                }
            }
        };
        var publisher = new FakePublisher();
        var orchestrator = CreateOrchestrator(client, publisher, now, tenants: TwoTenants());

        var result = await orchestrator.TriggerMeterReadingAsync(
            new MeterReadingTriggerRequest("meter-1", null, null, "tenant-2"), CancellationToken.None);

        Assert.Equal(1, result.ReadingCount);
        Assert.Empty(client.DiscoveredTenants);
        Assert.Equal("tenant-2", Assert.Single(client.FetchedTenants).TenantId);
        Assert.Equal("tenant-2", Assert.Single(publisher.Publications).CommunityId);
    }

    [Fact]
    public async Task TriggerMeterReadingAsync_WithoutTenantSelectsAllTenants()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var client = new FakeEegFakturaClient([])
        {
            MeterPointsByTenant = new Dictionary<string, IReadOnlyList<EegMeterPoint>>
            {
                ["tenant-1"] = [new EegMeterPoint("meter-1", null)],
                ["tenant-2"] = [new EegMeterPoint("meter-2", null)]
            }
        };
        var orchestrator = CreateOrchestrator(client, new FakePublisher(), now, tenants: TwoTenants());

        await orchestrator.TriggerMeterReadingAsync(
            new MeterReadingTriggerRequest(null, null, null), CancellationToken.None);

        Assert.Equal(["tenant-1", "tenant-2"], client.DiscoveredTenants);
        Assert.Equal(["tenant-1", "tenant-2"], client.FetchedTenants.Select(tenant => tenant.TenantId));
    }

    [Fact]
    public async Task TriggerMeterReadingAsync_RejectsSingleMeterWithoutTenant()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var client = new FakeEegFakturaClient([]);
        var orchestrator = CreateOrchestrator(client, new FakePublisher(), now, tenants: TwoTenants());

        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.TriggerMeterReadingAsync(
            new MeterReadingTriggerRequest("meter-1", null, null), CancellationToken.None));

        Assert.Empty(client.FetchedTenants);
    }

    [Fact]
    public async Task BackfillAsync_FetchesEachTenantSeparately()
    {
        var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(1);
        var client = new FakeEegFakturaClient([])
        {
            MeterPointsByTenant = new Dictionary<string, IReadOnlyList<EegMeterPoint>>
            {
                ["tenant-1"] = [new EegMeterPoint("meter-1", from)],
                ["tenant-2"] = [new EegMeterPoint("meter-1", from)]
            }
        };
        var orchestrator = CreateOrchestrator(client, new FakePublisher(), to, tenants: TwoTenants());

        var result = await orchestrator.BackfillAsync(from, to, CancellationToken.None);

        Assert.Equal(2, result.MeterCount);
        Assert.Equal(2, result.Requests);
        Assert.Equal(["tenant-1", "tenant-2"], client.FetchedTenants.Select(tenant => tenant.TenantId));
    }

    private static List<EegFakturaTenantOptions> TwoTenants() =>
    [
        new() { TenantId = "tenant-1", EcId = "ec-1", Username = "username-1", Password = "password-1", CommunityName = "Community 1" },
        new() { TenantId = "tenant-2", EcId = "ec-2", Username = "username-2", Password = "password-2", CommunityName = "Community 2" }
    ];

    private static EegFakturaSyncOrchestrator CreateOrchestrator(
        FakeEegFakturaClient client,
        FakePublisher publisher,
        DateTimeOffset now,
        int chunkDays = 7,
        List<EegFakturaTenantOptions>? tenants = null) =>
        new(
            client,
            publisher,
            Options.Create(new EegFakturaOptions
            {
                TenantId = "tenant-1",
                EcId = "ec-1",
                Username = "username-1",
                Password = "password-1",
                CommunityName = "Community",
                Tenants = tenants ?? [],
                LookbackDays = 28,
                BackfillChunkDays = chunkDays
            }),
            new FixedTimeProvider(now),
            NullLogger<EegFakturaSyncOrchestrator>.Instance);

    private sealed class FakeEegFakturaClient(IReadOnlyList<EegMeterPoint> meterPoints) : IEegFakturaClient
    {
        public IReadOnlyDictionary<string, IReadOnlyList<EegMeterPoint>>? MeterPointsByTenant { get; init; }

        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<EegRawReading>>>? ReadingsByTenant { get; init; }

        public IReadOnlyDictionary<string, IReadOnlyList<EegRawReading>> Readings { get; init; } =
            new Dictionary<string, IReadOnlyList<EegRawReading>>();

        public List<string> DiscoveredTenants { get; } = [];

        public List<EegFakturaTenantOptions> FetchedTenants { get; } = [];

        public long Start { get; private set; }

        public long End { get; private set; }

        public int MeterDiscoveryCalls { get; private set; }

        public IReadOnlyList<string> MeterIds { get; private set; } = [];

        public List<(long Start, long End)> Ranges { get; } = [];

        public Task<IReadOnlyList<EegMeterPoint>> GetMeterPointsAsync(
            EegFakturaTenantOptions tenant,
            CancellationToken cancellationToken)
        {
            MeterDiscoveryCalls++;
            DiscoveredTenants.Add(tenant.TenantId);
            return Task.FromResult(MeterPointsByTenant is null ? meterPoints : MeterPointsByTenant[tenant.TenantId]);
        }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<EegRawReading>>> FetchRawReadingsAsync(
            EegFakturaTenantOptions tenant,
            IReadOnlyList<string> meterIds,
            long startUnixMilliseconds,
            long endUnixMilliseconds,
            CancellationToken cancellationToken)
        {
            Start = startUnixMilliseconds;
            End = endUnixMilliseconds;
            MeterIds = meterIds;
            FetchedTenants.Add(tenant);
            Ranges.Add((startUnixMilliseconds, endUnixMilliseconds));
            return Task.FromResult(ReadingsByTenant is null ? Readings : ReadingsByTenant[tenant.TenantId]);
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
