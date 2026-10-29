using System.ComponentModel.DataAnnotations;

namespace EGWNInterfaceEEGFaktura.Application.Options;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    [Required]
    public string HostName { get; set; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; set; } = 5672;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string VirtualHost { get; set; } = "/";

    [Required]
    public string Exchange { get; set; } = "eda.sync";

    [Required]
    public string ExchangeType { get; set; } = "topic";

    [Required]
    public string RoutingKey { get; set; } = "eda.sync.result";

    public string QueueName { get; set; } = "egwn.measurements";

    public string DeadLetterQueue { get; set; } = "egwn.measurements.dlq";

    public int? MessageTtlMilliseconds { get; set; }
}
