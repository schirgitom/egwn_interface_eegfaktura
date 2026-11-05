namespace EGWNInterfaceEEGFaktura.Application.Abstractions;

public interface IEegFakturaMetrics
{
    void RecordMeterReading(string meterId, string tenantId, int readingCount, TimeSpan duration, bool success);

    void RecordBackfillStarted(int itemCount);

    void UpdateBackfillProgress(int completed, int failed, int remaining);

    void RecordBackfillCompleted(int itemCount, int completed, int failed, int remaining, int readings, TimeSpan duration);

    void SetJobSchedule(string jobName, DateTimeOffset? nextFireUtc, DateTimeOffset? previousFireUtc);
}
