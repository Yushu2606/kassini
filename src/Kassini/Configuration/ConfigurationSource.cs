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
            return ConfigurationSource.Parse(configStream);
        }
    }

    public static ConfigurationSource Parse(Stream sourceStream)
    {
        using var streamReader = new StreamReader(sourceStream);
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
        var configurationSection = deserializer.Deserialize<ConfigurationSection>(streamReader);

        return new ConfigurationSource(configurationSection); 
    }

    public ConfigurationSection ConfigurationSection { get; set; }
}
