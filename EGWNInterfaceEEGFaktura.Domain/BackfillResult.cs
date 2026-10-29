namespace EGWNInterfaceEEGFaktura.Domain;

public sealed record BackfillResult(
    DateTimeOffset From,
    DateTimeOffset To,
    int MeterCount,
    int ReadingCount,
    int Requests);
