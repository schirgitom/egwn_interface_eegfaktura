namespace EGWNInterfaceEEGFaktura.Domain;

public sealed record EegMeterPoint(string MeterPointNumber, DateTimeOffset? ActiveSince);
