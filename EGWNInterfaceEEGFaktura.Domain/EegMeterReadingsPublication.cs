namespace EGWNInterfaceEEGFaktura.Domain;

public sealed record EegMeterReadingsPublication(
    string MeterPointNumber,
    string CommunityId,
    string CommunityName,
    IReadOnlyList<EegMeterReading> Readings,
    DateTimeOffset CreatedAtUtc);
