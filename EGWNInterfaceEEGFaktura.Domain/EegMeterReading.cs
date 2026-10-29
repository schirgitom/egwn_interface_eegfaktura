namespace EGWNInterfaceEEGFaktura.Domain;

public sealed record EegMeterReading(
    DateTimeOffset Timestamp,
    decimal? TotalConsumptionValue,
    decimal? GridShareValue,
    decimal? CommunityShareValue);
