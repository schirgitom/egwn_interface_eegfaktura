using EGWNInterfaceEEGFaktura.Application.Abstractions;
using Prometheus;

namespace EGWNInterfaceEEGFaktura.Telemetry;

public sealed class PrometheusEegFakturaMetrics : IEegFakturaMetrics
{
    private static readonly string[] MeterLabels = ["meter_id", "tenant_id"];
    private static readonly string[] MeterStatusLabels = ["meter_id", "tenant_id", "status"];
    private static readonly string[] JobLabels = ["job"];

    private static readonly Gauge MeterLastSuccessTs = Metrics.CreateGauge(
        "eegfaktura_meter_reading_last_success_timestamp_seconds",
        "Unix timestamp of the last successful meter reading fetch and publication, per meter.",
        MeterLabels);

    private static readonly Gauge MeterLastAttemptTs = Metrics.CreateGauge(
        "eegfaktura_meter_reading_last_attempt_timestamp_seconds",
        "Unix timestamp of the last meter reading attempt, per meter.",
        MeterLabels);

    private static readonly Gauge MeterLastPoints = Metrics.CreateGauge(
        "eegfaktura_meter_reading_last_points",
        "Number of readings published for the meter in the last successful attempt.",
        MeterLabels);

    private static readonly Counter MeterReadingsTotal = Metrics.CreateCounter(
        "eegfaktura_meter_reading_total",
        "Total meter reading attempts by status (success/failure).",
        MeterStatusLabels);

    private static readonly Histogram MeterReadingDuration = Metrics.CreateHistogram(
        "eegfaktura_meter_reading_duration_seconds",
        "Duration of the shared raw-data fetch and meter publication, in seconds.",
        new HistogramConfiguration
        {
            LabelNames = MeterLabels,
            Buckets = Histogram.ExponentialBuckets(0.5, 2, 10)
        });

    private static readonly Gauge BackfillInProgress = Metrics.CreateGauge(
        "eegfaktura_backfill_in_progress",
        "1 while a backfill is running, 0 otherwise.");

    private static readonly Gauge BackfillItemTotal = Metrics.CreateGauge(
        "eegfaktura_backfill_item_total",
        "Number of tenant/time-window raw-data requests in the current or last backfill.");

    private static readonly Gauge BackfillItemsCompleted = Metrics.CreateGauge(
        "eegfaktura_backfill_items_completed",
        "Number of tenant/time-window requests successfully completed.");

    private static readonly Gauge BackfillItemsFailed = Metrics.CreateGauge(
        "eegfaktura_backfill_items_failed",
        "Number of tenant/time-window requests that failed.");

    private static readonly Gauge BackfillItemsRemaining = Metrics.CreateGauge(
        "eegfaktura_backfill_items_remaining",
        "Number of tenant/time-window requests not yet attempted.");

    private static readonly Gauge BackfillLastCompletedTs = Metrics.CreateGauge(
        "eegfaktura_backfill_last_completed_timestamp_seconds",
        "Unix timestamp of the last finished backfill (including failed or cancelled runs).");

    private static readonly Gauge BackfillLastDuration = Metrics.CreateGauge(
        "eegfaktura_backfill_last_duration_seconds",
        "Duration of the last finished backfill, in seconds.");

    private static readonly Gauge BackfillLastReadings = Metrics.CreateGauge(
        "eegfaktura_backfill_last_readings",
        "Number of readings published in the last finished backfill.");

    private static readonly Gauge JobNextFireTs = Metrics.CreateGauge(
        "eegfaktura_job_next_fire_timestamp_seconds",
        "Unix timestamp of the next scheduled Quartz job execution, 0 if none.",
        JobLabels);

    private static readonly Gauge JobPreviousFireTs = Metrics.CreateGauge(
        "eegfaktura_job_previous_fire_timestamp_seconds",
        "Unix timestamp of the previous Quartz job execution, 0 if none.",
        JobLabels);

    public void RecordMeterReading(string meterId, string tenantId, int readingCount, TimeSpan duration, bool success)
    {
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MeterLastAttemptTs.WithLabels(meterId, tenantId).Set(nowSeconds);
        MeterReadingsTotal.WithLabels(meterId, tenantId, success ? "success" : "failure").Inc();
        MeterReadingDuration.WithLabels(meterId, tenantId).Observe(duration.TotalSeconds);

        if (success)
        {
            MeterLastSuccessTs.WithLabels(meterId, tenantId).Set(nowSeconds);
            MeterLastPoints.WithLabels(meterId, tenantId).Set(readingCount);
        }
    }

    public void RecordBackfillStarted(int itemCount)
    {
        BackfillInProgress.Set(1);
        BackfillItemTotal.Set(itemCount);
        UpdateBackfillProgress(0, 0, itemCount);
    }

    public void UpdateBackfillProgress(int completed, int failed, int remaining)
    {
        BackfillItemsCompleted.Set(completed);
        BackfillItemsFailed.Set(failed);
        BackfillItemsRemaining.Set(remaining);
    }

    public void RecordBackfillCompleted(int itemCount, int completed, int failed, int remaining, int readings, TimeSpan duration)
    {
        BackfillInProgress.Set(0);
        BackfillItemTotal.Set(itemCount);
        UpdateBackfillProgress(completed, failed, remaining);
        BackfillLastCompletedTs.Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        BackfillLastDuration.Set(duration.TotalSeconds);
        BackfillLastReadings.Set(readings);
    }

    public void SetJobSchedule(string jobName, DateTimeOffset? nextFireUtc, DateTimeOffset? previousFireUtc)
    {
        JobNextFireTs.WithLabels(jobName).Set(nextFireUtc?.ToUnixTimeSeconds() ?? 0);
        JobPreviousFireTs.WithLabels(jobName).Set(previousFireUtc?.ToUnixTimeSeconds() ?? 0);
    }
}
