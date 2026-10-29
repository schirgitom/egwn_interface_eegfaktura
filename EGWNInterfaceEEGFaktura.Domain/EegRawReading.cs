namespace EGWNInterfaceEEGFaktura.Domain;

public sealed record EegRawReading(
    DateTimeOffset Timestamp,
    decimal? TotalValue,
    decimal? CommunityShareValue);
