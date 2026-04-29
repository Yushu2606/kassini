using Kassini.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Kassini.Configuration;

public class ConfigurationSource
{
    internal ConfigurationSource(ConfigurationSection configurationSection) 
    {
        ConfigurationSection = configurationSection;
    }

    public static ConfigurationSource Parse(string configFilePath)
    {
        using (var configStream = File.OpenRead(configFilePath))
        {
            return Path.GetExtension(configFilePath).ToLowerInvariant() switch
            {
                ".json" => ParseJson(configStream),
                ".yml" or ".yaml" => ParseYaml(configStream),
                _ => throw new NotSupportedException("Unrecognized configuration file extension"),
            };
        }
    }

    public static ConfigurationSource ParseYaml(Stream sourceStream)
    {
        using var streamReader = new StreamReader(sourceStream);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithNodeTypeResolver(new ReadOnlyCollectionNodeTypeResolver())
            .IgnoreUnmatchedProperties().Build();
        var configurationSection = deserializer.Deserialize<ConfigurationSection>(streamReader);

        return new ConfigurationSource(configurationSection);
    }

    public static ConfigurationSource ParseJson(Stream sourceStream)
    {
        var jsonSerializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters =
            {
                new JsonStringEnumConverter<AuthenticationMode>()
            }
        };

        var configurationSection = JsonSerializer.Deserialize<ConfigurationSection>(sourceStream, jsonSerializerOptions);

        return new ConfigurationSource(configurationSection ?? new ConfigurationSection());
    }

    public ConfigurationSection ConfigurationSection { get; set; }
}
