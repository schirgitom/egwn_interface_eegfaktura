namespace EGWNInterfaceEEGFaktura.Domain;

public sealed record MeterReadingTriggerResponse(
    string? MeterId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int ReadingCount);
