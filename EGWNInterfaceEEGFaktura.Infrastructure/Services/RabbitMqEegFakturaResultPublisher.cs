using System.Text;
using System.Text.Json;
using EGWNInterfaceEEGFaktura.Application.Abstractions;
using EGWNInterfaceEEGFaktura.Application.Options;
using EGWNInterfaceEEGFaktura.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace EGWNInterfaceEEGFaktura.Infrastructure.Services;

public sealed class RabbitMqEegFakturaResultPublisher : IEegFakturaResultPublisher, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqEegFakturaResultPublisher> _logger;
    private readonly object _sync = new();
    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqEegFakturaResultPublisher(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqEegFakturaResultPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task PublishAsync(EegMeterReadingsPublication publication, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var payload = JsonSerializer.Serialize(publication, SerializerOptions);
        var body = Encoding.UTF8.GetBytes(payload);

        lock (_sync)
        {
            var channel = EnsureChannel();
            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;
            properties.ContentType = "application/json";
            properties.MessageId = Guid.NewGuid().ToString("N");
            properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (_options.MessageTtlMilliseconds is > 0)
            {
                properties.Expiration = _options.MessageTtlMilliseconds.Value.ToString();
            }

            channel.BasicPublish(_options.Exchange, _options.RoutingKey, properties, body);
        }

        _logger.LogInformation(
            "Published EEGFaktura readings for meter {MeterPointNumber} to exchange {Exchange} with routing key {RoutingKey}",
            publication.MeterPointNumber,
            _options.Exchange,
            _options.RoutingKey);

        return Task.CompletedTask;
    }

    private IModel EnsureChannel()
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        _channel?.Dispose();
        _connection?.Dispose();

        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.ExchangeDeclare(_options.Exchange, _options.ExchangeType, durable: true, autoDelete: false);
        _logger.LogInformation("Connected RabbitMQ publisher to {Host}:{Port}", _options.HostName, _options.Port);
        return _channel;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _channel?.Dispose();
            _connection?.Dispose();
            _channel = null;
            _connection = null;
        }
    }
}
