using System.ComponentModel.DataAnnotations;

namespace EGWNInterfaceEEGFaktura.Application.Options;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    [Required]
    public string HostName { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 5672;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string VirtualHost { get; set; } = "/";

    public string Exchange { get; set; } = string.Empty;

    public string RoutingKey { get; set; } = string.Empty;
}
