using Consul;
using EGWNInterfaceEEGFaktura.Application.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EGWNInterfaceEEGFaktura.Infrastructure.Hosting;

public sealed class ConsulRegistrationHostedService : BackgroundService
{
    private readonly ILogger<ConsulRegistrationHostedService> _logger;
    private readonly ConsulOptions _options;
    private readonly IConsulClient? _consulClient;

    public ConsulRegistrationHostedService(
        IOptions<ConsulOptions> options,
        ILogger<ConsulRegistrationHostedService> logger)
    {
        _logger = logger;
        _options = options.Value;

        if (_options.Enabled)
        {
            _consulClient = new ConsulClient(config => config.Address = new Uri(_options.Address!, UriKind.Absolute));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_consulClient is null)
        {
            _logger.LogInformation("Consul registration is disabled");
            return;
        }

        var registration = new AgentServiceRegistration
        {
            ID = _options.ServiceId,
            Name = _options.ServiceName,
            Tags = _options.Tags,
            Address = Environment.MachineName,
            Port = 0,
            Check = new AgentServiceCheck
            {
                CheckID = $"{_options.ServiceId}:ttl",
                TTL = _options.Ttl,
                DeregisterCriticalServiceAfter = _options.Ttl + _options.Ttl
            }
        };

        await _consulClient.Agent.ServiceDeregister(registration.ID, stoppingToken);
        await _consulClient.Agent.ServiceRegister(registration, stoppingToken);
        _logger.LogInformation("Registered service {ServiceId} in Consul", registration.ID);

        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await _consulClient.Agent.PassTTL(registration.Check.CheckID, "healthy", stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_consulClient is not null)
            {
                await _consulClient.Agent.ServiceDeregister(_options.ServiceId, cancellationToken);
            }
        }
        finally
        {
            _consulClient?.Dispose();
            await base.StopAsync(cancellationToken);
        }
    }
}
