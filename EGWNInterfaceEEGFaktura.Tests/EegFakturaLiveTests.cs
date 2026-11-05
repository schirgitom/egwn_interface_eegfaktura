using EGWNInterfaceEEGFaktura.Application.Options;
using EGWNInterfaceEEGFaktura.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Xml.Linq;
using Xunit.Abstractions;

namespace EGWNInterfaceEEGFaktura.Tests;

public sealed class EegFakturaLiveTests(ITestOutputHelper output)
{
    [LiveFact]
    [Trait("Category", "Live")]
    public async Task BasicLoginAndRawRead_ReturnDataForConfiguredMeter()
    {
        var username = GetSetting("EEGFAKTURA_LIVE_USERNAME");
        var password = GetSetting("EEGFAKTURA_LIVE_PASSWORD");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "Set EEGFAKTURA_LIVE_USERNAME and EEGFAKTURA_LIVE_PASSWORD in live.runsettings or the test environment.");
        }

        var settings = new EegFakturaOptions
        {
            BaseUrl = GetSetting("EEGFAKTURA_LIVE_BASE_URL", "https://eeg-faktura.at"),
            TimeoutSeconds = 60
        };
        var tenant = new EegFakturaTenantOptions
        {
            Username = username,
            Password = password,
            TenantId = GetSetting("EEGFAKTURA_LIVE_TENANT_ID", "RC101900"),
            EcId = GetSetting("EEGFAKTURA_LIVE_EC_ID", "AT00800000000RC101900000000000138")
        };

        using var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"{settings.BaseUrl.TrimEnd('/')}/"),
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds)
        };
        var client = new EegFakturaClient(
            httpClient,
            NullLogger<EegFakturaClient>.Instance);
        var meterId = GetSetting(
            "EEGFAKTURA_LIVE_METER_ID",
            "AT0080000816000000202009030075815");
        var meterPoints = await client.GetMeterPointsAsync(tenant, CancellationToken.None);
        Assert.Contains(meterPoints, meter => string.Equals(
            meter.MeterPointNumber,
            meterId,
            StringComparison.OrdinalIgnoreCase));

        var start = GetUnixMilliseconds("EEGFAKTURA_LIVE_START", 1793055600000);
        var end = GetUnixMilliseconds("EEGFAKTURA_LIVE_END", 1793141999999);
        var response = await client.FetchRawReadingsAsync(
            tenant,
            [meterId],
            start,
            end,
            CancellationToken.None);

        Assert.True(response.TryGetValue(meterId, out var readings));
        Assert.NotEmpty(readings!);
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public Task RC101900_CanDiscoverMetersAndFetchRawData() =>
        VerifyConfiguredTenantAsync("RC101900");

    [LiveFact]
    [Trait("Category", "Live")]
    public Task RC103960_CanDiscoverMetersAndFetchRawData() =>
        VerifyConfiguredTenantAsync("RC103960");

    private async Task VerifyConfiguredTenantAsync(string tenantId)
    {
        var settingsPath = FindAppSettingsPath()
            ?? throw new InvalidOperationException("EGWNInterfaceEEGFaktura/appsettings.json was not found.");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(settingsPath)
            .AddEnvironmentVariables()
            .Build();
        var options = configuration.GetSection(EegFakturaOptions.SectionName).Get<EegFakturaOptions>()
            ?? throw new InvalidOperationException("EEGFaktura settings are missing.");
        var tenant = options.Tenants.SingleOrDefault(configuredTenant =>
            string.Equals(configuredTenant.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"EEGFaktura tenant '{tenantId}' is not configured.");
        if (string.IsNullOrWhiteSpace(tenant.TenantId)
            || string.IsNullOrWhiteSpace(tenant.EcId)
            || string.IsNullOrWhiteSpace(tenant.Username)
            || string.IsNullOrWhiteSpace(tenant.Password))
        {
            throw new InvalidOperationException($"EEGFaktura tenant '{tenantId}' is incomplete.");
        }

        using var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"{options.BaseUrl.TrimEnd('/')}/"),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };
        var client = new EegFakturaClient(httpClient, new LiveWarningLogger(output));
        var meters = await client.GetMeterPointsAsync(tenant, CancellationToken.None);
        Assert.True(meters.Count > 0, $"No meter points returned for tenant {tenant.TenantId}.");

        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-options.LookbackDays);
        var readings = await client.FetchRawReadingsAsync(
            tenant,
            meters.Select(meter => meter.MeterPointNumber).ToArray(),
            from.ToUnixTimeMilliseconds(),
            to.ToUnixTimeMilliseconds(),
            CancellationToken.None);

        Assert.NotEmpty(readings);
    }

    private static string GetSetting(string name, string defaultValue = "")
    {
        var environmentValue = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        var settingsValue = ReadRunSettingsValue(name);
        return string.IsNullOrWhiteSpace(settingsValue) ? defaultValue : settingsValue;
    }

    private static long GetUnixMilliseconds(string name, long defaultValue)
    {
        var value = GetSetting(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!long.TryParse(value, out var result))
        {
            throw new InvalidOperationException($"{name} must be a Unix timestamp in milliseconds.");
        }

        return result;
    }

    private static string? ReadRunSettingsValue(string name)
    {
        var settingsPath = FindRunSettingsPath();
        if (settingsPath is null)
        {
            return null;
        }

        var document = XDocument.Load(settingsPath);
        return document
            .Descendants("EnvironmentVariables")
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName.Equals(name, StringComparison.Ordinal))
            ?.Value;
    }

    private static string? FindRunSettingsPath()
    {
        var directories = new List<string>();
        AddParentDirectories(directories, AppContext.BaseDirectory);
        AddParentDirectories(directories, Environment.CurrentDirectory);

        return directories
            .Select(directory => Path.Combine(directory, "live.runsettings"))
            .FirstOrDefault(File.Exists);
    }

    private static string? FindAppSettingsPath()
    {
        var directories = new List<string>();
        AddParentDirectories(directories, AppContext.BaseDirectory);
        AddParentDirectories(directories, Environment.CurrentDirectory);

        return directories
            .Select(directory => Path.Combine(directory, "EGWNInterfaceEEGFaktura", "appsettings.json"))
            .FirstOrDefault(File.Exists);
    }

    private static void AddParentDirectories(List<string> directories, string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            directories.Add(directory.FullName);
        }
    }

    private sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (!string.Equals(
                    GetSetting("EEGFAKTURA_LIVE_TESTS"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                Skip = "Set EEGFAKTURA_LIVE_TESTS=true to enable the live EEGFaktura test.";
            }
        }
    }

    private sealed class LiveWarningLogger(ITestOutputHelper output) : ILogger<EegFakturaClient>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                output.WriteLine(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose() { }
        }
    }
}
