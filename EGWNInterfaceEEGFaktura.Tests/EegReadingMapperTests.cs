using System.Text.Json;
using EGWNInterfaceEEGFaktura.Application.Services;
using EGWNInterfaceEEGFaktura.Domain;

namespace EGWNInterfaceEEGFaktura.Tests;

public sealed class EegReadingMapperTests
{
    [Fact]
    public void Map_UsesEdaFieldNamesAndCalculatesGridShare()
    {
        var reading = new EegRawReading(
            DateTimeOffset.Parse("2026-10-27T09:00:00Z"),
            1.25m,
            0.4m);

        var publication = new EegMeterReadingsPublication(
            "meter-1",
            "tenant-1",
            "community",
            [EegReadingMapper.Map(reading)],
            DateTimeOffset.Parse("2026-10-29T12:00:00Z"));

        var json = JsonSerializer.Serialize(publication, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"totalConsumptionValue\":1.25", json);
        Assert.Contains("\"gridShareValue\":0.85", json);
        Assert.Contains("\"communityShareValue\":0.4", json);
        Assert.DoesNotContain("\"direction\"", json);
        Assert.DoesNotContain("\"qov\"", json);
    }

    [Fact]
    public void Map_WhenAValueIsMissing_DoesNotInventAGridShare()
    {
        var reading = new EegRawReading(
            DateTimeOffset.Parse("2026-10-27T09:00:00Z"),
            1.25m,
            null);

        var mapped = EegReadingMapper.Map(reading);

        Assert.Equal(1.25m, mapped.TotalConsumptionValue);
        Assert.Null(mapped.GridShareValue);
        Assert.Null(mapped.CommunityShareValue);
    }
}
