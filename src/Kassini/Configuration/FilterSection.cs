namespace Kassini.Configuration;

public class FilterSection
{
    public string? Name { get; set; } = null;
    public Dictionary<string, object> Properties { get; set; } = [];
}
