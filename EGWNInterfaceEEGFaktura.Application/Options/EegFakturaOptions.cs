using System.ComponentModel.DataAnnotations;

namespace EGWNInterfaceEEGFaktura.Application.Options;

public sealed class EegFakturaOptions
{
    public const string SectionName = "EegFaktura";

    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://eeg-faktura.at";

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string TenantId { get; set; } = "RC101900";

    [Required]
    public string EegId { get; set; } = string.Empty;

    public string? CommunityName { get; set; }

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 30;

    [Range(1, 365)]
    public int LookbackDays { get; set; } = 28;

    [Range(1, 31)]
    public int BackfillChunkDays { get; set; } = 7;
}
