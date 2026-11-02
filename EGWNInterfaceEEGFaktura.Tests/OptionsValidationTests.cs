using System.ComponentModel.DataAnnotations;
using EGWNInterfaceEEGFaktura.Application.Options;
using EGWNInterfaceEEGFaktura.Domain;

namespace EGWNInterfaceEEGFaktura.Tests;

public sealed class OptionsValidationTests
{
    [Fact]
    public void EegFakturaOptions_RequiresAnAbsoluteBaseUrl()
    {
        var options = new EegFakturaOptions { BaseUrl = "relative/path" };

        var validationResults = Validate(options);

        Assert.Contains(validationResults, result => result.MemberNames.Contains(nameof(EegFakturaOptions.BaseUrl)));
    }

    [Fact]
    public void RabbitMqOptions_RequiresAHostName()
    {
        var options = new RabbitMqOptions { HostName = string.Empty };

        var validationResults = Validate(options);

        Assert.Contains(validationResults, result => result.MemberNames.Contains(nameof(RabbitMqOptions.HostName)));
    }

    [Fact]
    public void MeterReadingTriggerRequest_RejectsEmptyMeterIdAndReversedDates()
    {
        var from = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
        var request = new MeterReadingTriggerRequest(" ", from, from.AddDays(-1));

        var validationResults = Validate(request);

        Assert.Contains(validationResults, result => result.MemberNames.Contains(nameof(MeterReadingTriggerRequest.MeterId)));
        Assert.Contains(validationResults, result => result.MemberNames.Contains(nameof(MeterReadingTriggerRequest.FromUtc)));
    }

    [Fact]
    public void MeterReadingTriggerRequest_RequiresTenantForSingleMeter()
    {
        var request = new MeterReadingTriggerRequest("meter-1", null, null);

        Assert.Contains(Validate(request), result =>
            result.MemberNames.Contains(nameof(MeterReadingTriggerRequest.TenantId)));
    }

    [Fact]
    public void EegFakturaOptions_AcceptsMultipleCompleteTenantsWithoutLegacyCredentials()
    {
        var options = new EegFakturaOptions
        {
            Tenants =
            [
                new() { TenantId = "RC1", EcId = "AT001", Username = "user-1", Password = "pass-1" },
                new() { TenantId = "RC2", EcId = "AT002", Username = "user-2", Password = "pass-2" }
            ]
        };

        Assert.Empty(Validate(options));
        Assert.Equal("AT002", options.ResolveTenant("rc2").EcId);
    }

    [Fact]
    public void EegFakturaOptions_RejectsIncompleteAndDuplicateTenants()
    {
        var options = new EegFakturaOptions
        {
            Tenants =
            [
                new() { TenantId = "RC1", EcId = "AT001", Username = "user-1", Password = "pass-1" },
                new() { TenantId = "rc1", EcId = "AT002", Username = "", Password = "pass-2" }
            ]
        };

        Assert.Equal(2, Validate(options).Count);
    }

    private static IReadOnlyCollection<ValidationResult> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return results;
    }
}
