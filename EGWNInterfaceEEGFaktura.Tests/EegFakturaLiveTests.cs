using EGWNInterfaceEEGFaktura.Application.Options;
using EGWNInterfaceEEGFaktura.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Xml.Linq;

namespace EGWNInterfaceEEGFaktura.Tests;

public sealed class EegFakturaLiveTests
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
            Username = username,
            Password = password,
            TenantId = GetSetting("EEGFAKTURA_LIVE_TENANT_ID", "RC101900"),
            EegId = GetSetting("EEGFAKTURA_LIVE_EEG_ID", "AT00800000000RC101900000000000138"),
            TimeoutSeconds = 60
        };

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri($"{settings.BaseUrl.TrimEnd('/')}/"),
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds)
        };
        var client = new EegFakturaClient(
            httpClient,
            Options.Create(settings),
            NullLogger<EegFakturaClient>.Instance);
        var meterId = GetSetting(
            "EEGFAKTURA_LIVE_METER_ID",
            "AT0080000816000000202009030075815");
        var meterPoints = await client.GetMeterPointsAsync(settings.TenantId, CancellationToken.None);
        Assert.Contains(meterPoints, meter => string.Equals(
            meter.MeterPointNumber,
            meterId,
            StringComparison.OrdinalIgnoreCase));

        var start = GetUnixMilliseconds("EEGFAKTURA_LIVE_START", 1793055600000);
        var end = GetUnixMilliseconds("EEGFAKTURA_LIVE_END", 1793141999999);
        var response = await client.FetchRawReadingsAsync(
            settings.EegId,
            settings.TenantId,
            [meterId],
            start,
            end,
            CancellationToken.None);

        Assert.True(response.TryGetValue(meterId, out var readings));
        Assert.NotEmpty(readings!);
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
}
