using System.ComponentModel.DataAnnotations;
using EGWNInterfaceEEGFaktura.Application.Options;

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

    private static IReadOnlyCollection<ValidationResult> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return results;
    }
}
