using EGWNInterfaceEEGFaktura.Application.Abstractions;
using Quartz;

namespace EGWNInterfaceEEGFaktura.Jobs;

[DisallowConcurrentExecution]
public sealed class EegFakturaSyncJob(
    IEegFakturaSyncOrchestrator orchestrator,
    ILogger<EegFakturaSyncJob> logger) : IJob
{
    public const string JobName = "eegfaktura-sync";
    public const string GroupName = "integration";

    public async Task Execute(IJobExecutionContext context)
    {
        logger.LogInformation("EEGFaktura synchronization started");
        var result = await orchestrator.SyncAsync(context.CancellationToken);
        logger.LogInformation(
            "EEGFaktura synchronization completed: {MeterCount} meters, {ReadingCount} readings for {From}–{To}. Next run: {NextFire}",
            result.MeterCount,
            result.ReadingCount,
            result.From,
            result.To,
            context.NextFireTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "unknown");
    }
}
