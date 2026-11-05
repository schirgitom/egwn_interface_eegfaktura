using EGWNInterfaceEEGFaktura.Application.Options;
using EGWNInterfaceEEGFaktura.Application.Abstractions;
using EGWNInterfaceEEGFaktura.Application.Services;
using EGWNInterfaceEEGFaktura.Infrastructure.Services;
using EGWNInterfaceEEGFaktura.Infrastructure.Hosting;
using EGWNInterfaceEEGFaktura.Jobs;
using EGWNInterfaceEEGFaktura.Telemetry;
using Microsoft.Extensions.Options;
using Prometheus;
using Quartz;
using Serilog;
using AppQuartzOptions = EGWNInterfaceEEGFaktura.Application.Options.QuartzOptions;

var builder = WebApplication.CreateBuilder(args);

var consulUrl = Environment.GetEnvironmentVariable("CONSUL_URL")
    ?? builder.Configuration["Consul:Address"];
var consulKvPath = Environment.GetEnvironmentVariable("CONSUL_KV_PATH")
    ?? builder.Configuration["Consul:Key"]
    ?? builder.Configuration["Consul:KvPath"]
    ?? "egwn-interface-eegfaktura/appsettings";

if (!string.IsNullOrWhiteSpace(consulUrl))
{
    builder.Configuration.AddConsulKv(consulUrl, consulKvPath);
    if (string.IsNullOrWhiteSpace(builder.Configuration["Consul:Address"]))
    {
        builder.Configuration["Consul:Address"] = consulUrl;
    }

    Console.WriteLine($"[Startup] Loaded configuration from Consul key '{consulKvPath}'");
}

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

builder.Services.AddOptions<ConsulOptions>()
    .BindConfiguration(ConsulOptions.SectionName)
    .Validate(options => !options.Enabled || Uri.TryCreate(options.Address, UriKind.Absolute, out var address)
        && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps),
        "Consul:Address must be an absolute HTTP(S) URL when registration is enabled.")
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ServiceId)
        && !string.IsNullOrWhiteSpace(options.ServiceName)
        && options.Ttl > TimeSpan.Zero
        && options.HeartbeatInterval > TimeSpan.Zero
        && options.HeartbeatInterval < options.Ttl,
        "Consul service ID and name are required, and heartbeat interval must be between zero and TTL.")
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
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

builder.Services.AddSingleton<IEegFakturaResultPublisher, RabbitMqEegFakturaResultPublisher>();
builder.Services.AddSingleton<IEegFakturaSyncOrchestrator, EegFakturaSyncOrchestrator>();
builder.Services.AddSingleton<IEegFakturaMetrics, PrometheusEegFakturaMetrics>();
builder.Services.AddHostedService<ConsulRegistrationHostedService>();
builder.Services.AddHostedService<QuartzScheduleMetricsHostedService>();

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
foreach (var tenant in eegFakturaOptions.GetTenants().Where(tenant => tenant.Password.Contains(':')))
{
    app.Logger.LogWarning(
        "The EEGFaktura password for tenant {TenantId} contains ':'. Energystore Basic authentication does not support ':' in passwords.",
        tenant.TenantId);
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
