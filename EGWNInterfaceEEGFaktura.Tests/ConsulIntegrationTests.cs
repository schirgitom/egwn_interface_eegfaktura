using System.Collections.Concurrent;
using System.Text;
using EGWNInterfaceEEGFaktura.Application.Options;
using EGWNInterfaceEEGFaktura.Infrastructure.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EGWNInterfaceEEGFaktura.Tests;

public sealed class ConsulIntegrationTests
{
    [Fact]
    public async Task ConsulKv_OverridesSettingsAndFlattensTenantArrays()
    {
        var json = """
                   {
                     "EegFaktura": {
                       "Tenants": [
                         { "TenantId": "RC101900", "EcId": "ec-1" },
                         { "TenantId": "RC103960", "EcId": "ec-2" }
                       ]
                     }
                   }
                   """;
        await using var server = await StartServerAsync(app =>
            app.MapGet("/v1/kv/egwn-interface-eegfaktura/appsettings", () => Results.Json(new[]
            {
                new
                {
                    Key = "egwn-interface-eegfaktura/appsettings",
                    Value = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                }
            })));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EegFaktura:Tenants:0:TenantId"] = "previous"
            })
            .AddConsulKv(server.Urls.Single(), "egwn-interface-eegfaktura/appsettings")
            .Build();

        Assert.Equal("RC101900", configuration["EegFaktura:Tenants:0:TenantId"]);
        Assert.Equal("ec-2", configuration["EegFaktura:Tenants:1:EcId"]);
    }

    [Fact]
    public async Task ConsulKv_MissingKeyFailsStartup()
    {
        await using var server = await StartServerAsync(_ => { });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ConfigurationBuilder().AddConsulKv(server.Urls.Single(), "missing").Build());

        Assert.Contains("missing", exception.Message);
    }

    [Fact]
    public async Task ConsulRegistration_RegistersHeartbeatsAndDeregisters()
    {
        var requests = new ConcurrentQueue<string>();
        var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServerAsync(app =>
        {
            app.MapPut("/v1/agent/service/deregister/{id}", (string id) =>
            {
                requests.Enqueue($"deregister:{id}");
                return Results.Json(true);
            });
            app.MapPut("/v1/agent/service/register", () =>
            {
                requests.Enqueue("register");
                return Results.Json(true);
            });
            app.MapPut("/v1/agent/check/pass/{id}", (string id) =>
            {
                requests.Enqueue($"heartbeat:{id}");
                heartbeat.TrySetResult();
                return Results.Json(true);
            });
        });

        var service = new ConsulRegistrationHostedService(
            Options.Create(new ConsulOptions
            {
                Address = server.Urls.Single(),
                Enabled = true,
                ServiceId = "test-eegfaktura",
                HeartbeatInterval = TimeSpan.FromMilliseconds(100),
                Ttl = TimeSpan.FromSeconds(3)
            }),
            NullLogger<ConsulRegistrationHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal("deregister:test-eegfaktura", requests.First());
        Assert.Contains("register", requests);
        Assert.Contains("heartbeat:test-eegfaktura:ttl", requests);
        Assert.Equal("deregister:test-eegfaktura", requests.Last());
    }

    private static async Task<WebApplication> StartServerAsync(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        configure(app);
        await app.StartAsync();
        return app;
    }
}
