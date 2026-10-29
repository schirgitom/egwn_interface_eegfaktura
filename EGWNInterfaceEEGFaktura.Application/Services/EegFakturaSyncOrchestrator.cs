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
        var meterPoints = await GetMeterPointsAsync(cancellationToken);
        var readingCount = await FetchAndPublishAsync(meterPoints, from, to, cancellationToken);

        return new SyncResult(from, to, meterPoints.Count, readingCount);
    }

    public async Task<BackfillResult> BackfillAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var meterPoints = await GetMeterPointsAsync(cancellationToken);
        if (meterPoints.Count == 0)
        {
            var emptyTo = to ?? timeProvider.GetUtcNow();
            var emptyFrom = from ?? emptyTo;
            return new BackfillResult(emptyFrom, emptyTo, 0, 0, 0);
        }

        var end = to ?? timeProvider.GetUtcNow();
        if (!from.HasValue && meterPoints.Any(meterPoint => !meterPoint.ActiveSince.HasValue))
        {
            throw new InvalidOperationException(
                "EEGFaktura did not provide activation dates for every meter; pass the backfill start date explicitly.");
        }

        var start = from ?? meterPoints.Min(meterPoint => meterPoint.ActiveSince!.Value);

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

            readingCount += await FetchAndPublishAsync(meterPoints, chunkStart, chunkEnd, cancellationToken);
            requestCount++;
            chunkStart = chunkEnd.AddMilliseconds(1);
        }

        return new BackfillResult(start, end, meterPoints.Count, readingCount, requestCount);
    }

    private async Task<IReadOnlyList<EegMeterPoint>> GetMeterPointsAsync(CancellationToken cancellationToken)
    {
        var meterPoints = await eegFakturaClient.GetMeterPointsAsync(_options.TenantId, cancellationToken);
        if (meterPoints.Count == 0)
        {
            logger.LogWarning("No eligible EEGFaktura meter points found for tenant {TenantId}", _options.TenantId);
        }

        return meterPoints;
    }

    private async Task<int> FetchAndPublishAsync(
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
            _options.EegId,
            _options.TenantId,
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
                _options.TenantId,
                string.IsNullOrWhiteSpace(_options.CommunityName) ? _options.TenantId : _options.CommunityName,
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
