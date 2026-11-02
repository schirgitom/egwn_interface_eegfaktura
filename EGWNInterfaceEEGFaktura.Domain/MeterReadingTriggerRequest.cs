using System.ComponentModel.DataAnnotations;

namespace EGWNInterfaceEEGFaktura.Domain;

public sealed record MeterReadingTriggerRequest(
    string? MeterId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    string? TenantId = null) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MeterId is not null && string.IsNullOrWhiteSpace(MeterId))
        {
            yield return new ValidationResult("The meterId must not be empty.", [nameof(MeterId)]);
        }

        if (MeterId is not null && string.IsNullOrWhiteSpace(TenantId))
        {
            yield return new ValidationResult("A tenantId is required for a single meter.", [nameof(TenantId)]);
        }

        if (FromUtc > ToUtc)
        {
            yield return new ValidationResult("The fromUtc value must be earlier than or equal to toUtc.", [nameof(FromUtc), nameof(ToUtc)]);
        }
    }
}
