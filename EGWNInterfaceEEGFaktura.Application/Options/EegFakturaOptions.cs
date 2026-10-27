using System.ComponentModel.DataAnnotations;

namespace EGWNInterfaceEEGFaktura.Application.Options;

public sealed class EegFakturaOptions
{
    public const string SectionName = "EegFaktura";

    [Required]
    [Url]
    public string BaseUrl { get; set; } = string.Empty;

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 30;
}
