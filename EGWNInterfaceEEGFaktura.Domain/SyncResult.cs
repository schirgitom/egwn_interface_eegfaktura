namespace EGWNInterfaceEEGFaktura.Domain;

public sealed record SyncResult(
    DateTimeOffset From,
    DateTimeOffset To,
    int MeterCount,
    int ReadingCount);
