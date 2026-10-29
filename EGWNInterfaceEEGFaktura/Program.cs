using EGWNInterfaceEEGFaktura.Application.Options;
using EGWNInterfaceEEGFaktura.Application.Abstractions;
using EGWNInterfaceEEGFaktura.Application.Services;
using EGWNInterfaceEEGFaktura.Infrastructure.Services;
using EGWNInterfaceEEGFaktura.Jobs;
using Microsoft.Extensions.Options;
using Prometheus;
using Quartz;
using Serilog;
using AppQuartzOptions = EGWNInterfaceEEGFaktura.Application.Options.QuartzOptions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

builder.Services.AddOptions<EegFakturaOptions>()
    .BindConfiguration(EegFakturaOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<RabbitMqOptions>()
    .BindConfiguration(RabbitMqOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<AppQuartzOptions>()
    .BindConfiguration(AppQuartzOptions.SectionName)
    .Validate(options => options.IntervalMinutes > 0, "Quartz:IntervalMinutes must be greater than zero.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<IEegFakturaClient, EegFakturaClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<EegFakturaOptions>>().Value;
    client.BaseAddress = new Uri($"{options.BaseUrl.TrimEnd('/')}/", UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});

builder.Services.AddSingleton<IEegFakturaResultPublisher, RabbitMqEegFakturaResultPublisher>();
builder.Services.AddSingleton<IEegFakturaSyncOrchestrator, EegFakturaSyncOrchestrator>();

var quartzOptions = builder.Configuration.GetSection(AppQuartzOptions.SectionName).Get<AppQuartzOptions>()
    ?? new AppQuartzOptions();
builder.Services.AddQuartz(quartz =>
{
    var jobKey = new JobKey(EegFakturaSyncJob.JobName, EegFakturaSyncJob.GroupName);
    quartz.AddJob<EegFakturaSyncJob>(job => job.WithIdentity(jobKey));
    quartz.AddTrigger(trigger => trigger
        .ForJob(jobKey)
        .WithIdentity($"{EegFakturaSyncJob.JobName}.trigger", EegFakturaSyncJob.GroupName)
        .StartNow()
        .WithSimpleSchedule(schedule =>
            schedule.WithInterval(TimeSpan.FromMinutes(quartzOptions.IntervalMinutes)).RepeatForever()));
});
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

builder.Host.UseSerilog((context, loggerConfiguration) =>
    loggerConfiguration.ReadFrom.Configuration(context.Configuration));

var app = builder.Build();

var eegFakturaOptions = app.Services.GetRequiredService<IOptions<EegFakturaOptions>>().Value;
if (eegFakturaOptions.Password.Contains(':'))
{
    app.Logger.LogWarning(
        "The EEGFaktura password contains ':'. Energystore Basic authentication does not support ':' in passwords.");
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseRouting();
app.UseHttpMetrics();
app.MapControllers();
app.MapHealthChecks("/health");
app.MapMetrics();

await app.RunAsync();

public partial class Program;
