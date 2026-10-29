using EGWNInterfaceEEGFaktura.Domain;

namespace EGWNInterfaceEEGFaktura.Application.Services;

public static class EegReadingMapper
{
    public static EegMeterReading Map(EegRawReading reading)
    {
        decimal? gridShareValue = reading.TotalValue.HasValue && reading.CommunityShareValue.HasValue
            ? reading.TotalValue.Value - reading.CommunityShareValue.Value
            : null;

        return new EegMeterReading(
            reading.Timestamp,
            reading.TotalValue,
            gridShareValue,
            reading.CommunityShareValue);
    }
}
