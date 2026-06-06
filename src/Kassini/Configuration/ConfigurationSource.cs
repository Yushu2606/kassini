using Kassini.Authentication;
using SharpYaml;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        var yamlSerializerOptions = new YamlSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var configurationSection = YamlSerializer.Deserialize<ConfigurationSection>(sourceStream, yamlSerializerOptions);

        return new ConfigurationSource(configurationSection ?? new ConfigurationSection());
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
