using EGWNInterfaceEEGFaktura.Application.Abstractions;
using EGWNInterfaceEEGFaktura.Application.Options;
using EGWNInterfaceEEGFaktura.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EGWNInterfaceEEGFaktura.Application.Services;

public sealed class EegFakturaSyncOrchestrator(
    IEegFakturaClient eegFakturaClient,
    IEegFakturaResultPublisher resultPublisher,
    IOptions<EegFakturaOptions> options,
    TimeProvider timeProvider,
    ILogger<EegFakturaSyncOrchestrator> logger) : IEegFakturaSyncOrchestrator
{
    private readonly EegFakturaOptions _options = options.Value;

    public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var to = timeProvider.GetUtcNow();
        var from = to.AddDays(-_options.LookbackDays);
        var meterCount = 0;
        var readingCount = 0;
        foreach (var tenant in _options.GetTenants())
        {
            var meterPoints = await GetMeterPointsAsync(tenant, cancellationToken);
            meterCount += meterPoints.Count;
            readingCount += await FetchAndPublishAsync(tenant, meterPoints, from, to, cancellationToken);
        }

        return new SyncResult(from, to, meterCount, readingCount);
    }

    public async Task<MeterReadingTriggerResponse> TriggerMeterReadingAsync(
        MeterReadingTriggerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MeterId is not null && string.IsNullOrWhiteSpace(request.MeterId))
        {
            throw new ArgumentException("The meter ID must not be empty.", nameof(request));
        }

        if (request.MeterId is not null && string.IsNullOrWhiteSpace(request.TenantId))
        {
            throw new ArgumentException("A tenant ID is required for a single meter.", nameof(request));
        }

        var to = request.ToUtc ?? timeProvider.GetUtcNow();
        var from = request.FromUtc ?? to.AddDays(-_options.LookbackDays);
        if (from > to)
        {
            throw new ArgumentException("The trigger start must be earlier than or equal to the end.", nameof(request));
        }

        var tenants = request.TenantId is null
            ? _options.GetTenants()
            : [_options.ResolveTenant(request.TenantId)];
        var readingCount = 0;
        foreach (var tenant in tenants)
        {
            IReadOnlyList<EegMeterPoint> meterPoints = request.MeterId is null
                ? await GetMeterPointsAsync(tenant, cancellationToken)
                : [new EegMeterPoint(request.MeterId, null)];
            readingCount += await FetchAndPublishAsync(tenant, meterPoints, from, to, cancellationToken);
        }

        return new MeterReadingTriggerResponse(request.MeterId, from, to, readingCount);
    }

    public async Task<BackfillResult> BackfillAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var tenantMeters = new List<(EegFakturaTenantOptions Tenant, IReadOnlyList<EegMeterPoint> MeterPoints)>();
        foreach (var tenant in _options.GetTenants())
        {
            var meterPoints = await GetMeterPointsAsync(tenant, cancellationToken);
            if (meterPoints.Count > 0)
            {
                tenantMeters.Add((tenant, meterPoints));
            }
        }

        if (tenantMeters.Count == 0)
        {
            var emptyTo = to ?? timeProvider.GetUtcNow();
            var emptyFrom = from ?? emptyTo;
            return new BackfillResult(emptyFrom, emptyTo, 0, 0, 0);
        }

        var end = to ?? timeProvider.GetUtcNow();
        if (!from.HasValue && tenantMeters.Any(entry => entry.MeterPoints.Any(meterPoint => !meterPoint.ActiveSince.HasValue)))
        {
            throw new InvalidOperationException(
                "EEGFaktura did not provide activation dates for every meter; pass the backfill start date explicitly.");
        }

        var start = from ?? tenantMeters.Min(entry => entry.MeterPoints.Min(meterPoint => meterPoint.ActiveSince!.Value));

        if (start == default)
        {
            throw new InvalidOperationException(
                "EEGFaktura did not provide an activation date for any meter; pass the backfill start date explicitly.");
        }

        if (start > end)
        {
            throw new ArgumentException("The backfill start must be earlier than or equal to the end.", nameof(from));
        }

        var readingCount = 0;
        var requestCount = 0;
        var chunkStart = start;

        while (chunkStart <= end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkEnd = chunkStart.AddDays(_options.BackfillChunkDays);
            if (chunkEnd > end)
            {
                chunkEnd = end;
            }

            foreach (var (tenant, meterPoints) in tenantMeters)
            {
                readingCount += await FetchAndPublishAsync(tenant, meterPoints, chunkStart, chunkEnd, cancellationToken);
                requestCount++;
            }
            chunkStart = chunkEnd.AddMilliseconds(1);
        }

        return new BackfillResult(start, end, tenantMeters.Sum(entry => entry.MeterPoints.Count), readingCount, requestCount);
    }

    private async Task<IReadOnlyList<EegMeterPoint>> GetMeterPointsAsync(
        EegFakturaTenantOptions tenant,
        CancellationToken cancellationToken)
    {
        var meterPoints = await eegFakturaClient.GetMeterPointsAsync(tenant, cancellationToken);
        if (meterPoints.Count == 0)
        {
            logger.LogWarning("No eligible EEGFaktura meter points found for tenant {TenantId}", tenant.TenantId);
        }

        return meterPoints;
    }

    private async Task<int> FetchAndPublishAsync(
        EegFakturaTenantOptions tenant,
        IReadOnlyList<EegMeterPoint> meterPoints,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        if (meterPoints.Count == 0)
        {
            return 0;
        }

        var meterIds = meterPoints.Select(meterPoint => meterPoint.MeterPointNumber).ToArray();
        var rawReadings = await eegFakturaClient.FetchRawReadingsAsync(
            tenant,
            meterIds,
            from.ToUnixTimeMilliseconds(),
            to.ToUnixTimeMilliseconds(),
            cancellationToken);

        var readingCount = 0;
        foreach (var meterPoint in meterPoints)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!rawReadings.TryGetValue(meterPoint.MeterPointNumber, out var meterReadings)
                || meterReadings.Count == 0)
            {
                logger.LogDebug(
                    "EEGFaktura returned no readings for meter {MeterPointNumber} in {From}–{To}",
                    meterPoint.MeterPointNumber,
                    from,
                    to);
                continue;
            }

            var publication = new EegMeterReadingsPublication(
                meterPoint.MeterPointNumber,
                tenant.TenantId,
                string.IsNullOrWhiteSpace(tenant.CommunityName) ? tenant.TenantId : tenant.CommunityName,
                meterReadings.Select(EegReadingMapper.Map).ToArray(),
                timeProvider.GetUtcNow());

            await resultPublisher.PublishAsync(publication, cancellationToken);
            readingCount += publication.Readings.Count;
        }

        logger.LogInformation(
            "EEGFaktura sync processed {MeterCount} meters and {ReadingCount} readings for {From}–{To}",
            meterPoints.Count,
            readingCount,
            from,
            to);

        return readingCount;
    }
}
