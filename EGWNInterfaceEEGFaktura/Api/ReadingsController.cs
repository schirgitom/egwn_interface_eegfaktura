using EGWNInterfaceEEGFaktura.Application.Abstractions;
using EGWNInterfaceEEGFaktura.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EGWNInterfaceEEGFaktura.Api;

[ApiController]
[Route("api/readings")]
public sealed class ReadingsController(IEegFakturaSyncOrchestrator orchestrator) : ControllerBase
{
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
