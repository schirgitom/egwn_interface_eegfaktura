namespace EGWNInterfaceEEGFaktura.Application.Options;

public sealed class ConsulOptions
{
    public const string SectionName = "Consul";

    public string? Address { get; set; }

    public string KvPath { get; set; } = "egwn-interface-eegfaktura/appsettings";

    public string ServiceName { get; set; } = "egwn-interface-eegfaktura";

    public string ServiceId { get; set; } = $"{Environment.MachineName}-egwn-interface-eegfaktura";

    public string[] Tags { get; set; } = [];

    public bool Enabled { get; set; } = true;

    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);
}
