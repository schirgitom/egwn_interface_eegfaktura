using EGWNInterfaceEEGFaktura.Application.Abstractions;
using EGWNInterfaceEEGFaktura.Application.Options;
using EGWNInterfaceEEGFaktura.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EGWNInterfaceEEGFaktura.Api;

[ApiController]
[Route("api/readings")]
public sealed class ReadingsController(
    IEegFakturaSyncOrchestrator orchestrator,
    TimeProvider timeProvider,
    IOptions<EegFakturaOptions> options) : ControllerBase
{
    [HttpPost("meter")]
    public async Task<ActionResult<MeterReadingTriggerResponse>> TriggerMeterReading(
        [FromBody] MeterReadingTriggerRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MeterId is not null && string.IsNullOrWhiteSpace(request.TenantId))
        {
            return BadRequest(new ProblemDetails { Title = "A tenantId is required for a single meter" });
        }

        if (request.TenantId is not null && !options.Value.GetTenants().Any(tenant =>
                string.Equals(tenant.TenantId, request.TenantId, StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest(new ProblemDetails { Title = "Unknown EEGFaktura tenant" });
        }

        if (request.FromUtc is { } from && from > (request.ToUtc ?? timeProvider.GetUtcNow()))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid trigger range",
                Detail = "The 'fromUtc' value must be earlier than or equal to 'toUtc'."
            });
        }

        return Ok(await orchestrator.TriggerMeterReadingAsync(request, cancellationToken));
    }

    [HttpPost("meter/{meterId}")]
    public async Task<ActionResult<MeterReadingTriggerResponse>> TriggerMeterReadingByMeterId(
        [FromRoute] string meterId,
        [FromBody] MeterReadingTriggerRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(meterId))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid meter ID" });
        }

        return await TriggerMeterReading(request with { MeterId = meterId }, cancellationToken);
    }

    [HttpPost("backfill")]
    public async Task<ActionResult<BackfillResult>> Backfill(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        if (from.HasValue && to.HasValue && from > to)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid backfill range",
                Detail = "The 'from' value must be earlier than or equal to 'to'."
            });
        }

        return Ok(await orchestrator.BackfillAsync(from, to, cancellationToken));
    }
}
