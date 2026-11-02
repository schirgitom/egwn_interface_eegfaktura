using System.ComponentModel.DataAnnotations;

namespace EGWNInterfaceEEGFaktura.Application.Options;

public sealed class EegFakturaOptions : IValidatableObject
{
    public const string SectionName = "EegFaktura";

    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://eeg-faktura.at";

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string TenantId { get; set; } = "RC101900";

    public string EcId { get; set; } = string.Empty;

    public string? CommunityName { get; set; }

    public List<EegFakturaTenantOptions> Tenants { get; set; } = [];

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 30;

    [Range(1, 365)]
    public int LookbackDays { get; set; } = 28;

    [Range(1, 31)]
    public int BackfillChunkDays { get; set; } = 7;

    public IReadOnlyList<EegFakturaTenantOptions> GetTenants() =>
        Tenants.Count > 0
            ? Tenants
            : [new EegFakturaTenantOptions
            {
                TenantId = TenantId,
                EcId = EcId,
                Username = Username,
                Password = Password,
                CommunityName = CommunityName
            }];

    public EegFakturaTenantOptions ResolveTenant(string tenantId) =>
        GetTenants().FirstOrDefault(tenant =>
            string.Equals(tenant.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No EEGFaktura tenant is configured for '{tenantId}'.", nameof(tenantId));

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var tenants = GetTenants();
        if (tenants.Any(tenant =>
                string.IsNullOrWhiteSpace(tenant.TenantId)
                || string.IsNullOrWhiteSpace(tenant.EcId)
                || string.IsNullOrWhiteSpace(tenant.Username)
                || string.IsNullOrWhiteSpace(tenant.Password)))
        {
            yield return new ValidationResult(
                "Each EEGFaktura tenant requires TenantId, EcId, Username and Password.",
                [nameof(Tenants)]);
        }

        if (tenants.Select(tenant => tenant.TenantId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != tenants.Count)
        {
            yield return new ValidationResult("EEGFaktura tenant IDs must be unique.", [nameof(Tenants)]);
        }
    }
}
