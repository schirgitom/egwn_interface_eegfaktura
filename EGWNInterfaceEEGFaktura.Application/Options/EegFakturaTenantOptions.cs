namespace EGWNInterfaceEEGFaktura.Application.Options;

public sealed class EegFakturaTenantOptions
{
    public string TenantId { get; set; } = string.Empty;

    public string EcId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string? CommunityName { get; set; }
}
