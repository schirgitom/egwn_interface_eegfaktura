using EGWNInterfaceEEGFaktura.Application.Abstractions;
using EGWNInterfaceEEGFaktura.Jobs;
using Quartz;

namespace EGWNInterfaceEEGFaktura.Telemetry;

public sealed class QuartzScheduleMetricsHostedService(
    ISchedulerFactory schedulerFactory,
    IEegFakturaMetrics metrics,
    ILogger<QuartzScheduleMetricsHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var jobKey = new JobKey(EegFakturaSyncJob.JobName, EegFakturaSyncJob.GroupName);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var scheduler = await schedulerFactory.GetScheduler(stoppingToken);
                var triggers = await scheduler.GetTriggersOfJob(jobKey, stoppingToken);
                var nextFire = triggers.Select(trigger => trigger.GetNextFireTimeUtc())
                    .Where(next => next.HasValue).Min();
                var previousFire = triggers.Select(trigger => trigger.GetPreviousFireTimeUtc())
                    .Where(previous => previous.HasValue).Max();
                metrics.SetJobSchedule(jobKey.Name, nextFire, previousFire);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to update Quartz schedule metrics");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
