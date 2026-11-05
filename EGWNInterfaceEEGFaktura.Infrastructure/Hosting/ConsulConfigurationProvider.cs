using System.Text;
using System.Text.Json;
using Consul;
using Microsoft.Extensions.Configuration;

namespace EGWNInterfaceEEGFaktura.Infrastructure.Hosting;

public sealed class ConsulConfigurationProvider(string consulUrl, string kvPath) : ConfigurationProvider
{
    public override void Load()
    {
        using var client = new ConsulClient(config => config.Address = new Uri(consulUrl, UriKind.Absolute));
        var result = client.KV.Get(kvPath).GetAwaiter().GetResult();
        if (result.Response?.Value is null)
        {
            throw new InvalidOperationException($"Consul configuration key '{kvPath}' was not found or is empty.");
        }

        var json = Encoding.UTF8.GetString(result.Response.Value);
        Data = FlattenJson(json);
    }

    private static Dictionary<string, string?> FlattenJson(string json)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var docOptions = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        using var document = JsonDocument.Parse(json, docOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Consul configuration must contain a JSON object.");
        }

        Flatten(document.RootElement, string.Empty, result);
        return result;
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string?> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}:{property.Name}";
                    Flatten(property.Value, key, result);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, $"{prefix}:{index}", result);
                    index++;
                }
                break;

            default:
                result[prefix] = element.ValueKind == JsonValueKind.Null ? null : element.ToString();
                break;
        }
    }
}

public sealed class ConsulConfigurationSource(string consulUrl, string kvPath) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new ConsulConfigurationProvider(consulUrl, kvPath);
}

public static class ConsulConfigurationExtensions
{
    public static IConfigurationBuilder AddConsulKv(this IConfigurationBuilder builder, string consulUrl, string kvPath) =>
        builder.Add(new ConsulConfigurationSource(consulUrl, kvPath));
}
